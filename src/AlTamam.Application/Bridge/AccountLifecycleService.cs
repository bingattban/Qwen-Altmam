using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// 03.13: a historical person transition can never leave an independently
/// privileged active account or session behind. Executed in the very SAME
/// SQLCipher transaction as the movement/separation and immutable audit.
/// Reactivation is exclusively a separate, explicitly audited admin action.
/// </summary>
internal static class AccountLifecycleService
{
    internal static async Task<bool> WouldStrandFoundingAdministratorAsync(
        SqliteConnection c,SqliteTransaction tx,IReadOnlyCollection<string> personIds,
        CancellationToken ct)
    {
        foreach(string id in personIds)
        {
            using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT EXISTS(SELECT 1 FROM Users
                    WHERE LinkedPersonId=$person AND Role=1 AND ActiveFlag=1);
                """;
            cmd.Parameters.AddWithValue("$person",id);
            if(Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))==1)
                return true;
        }
        return false;
    }

    internal static async Task<int> SuspendAccountsForPersonnelExitAsync(
        SqliteConnection c,SqliteTransaction tx,string personId,string reasonCode,
        string actorUserId,string actorUsername,TimeProvider clock,string effectiveDate,CancellationToken ct)
    {
        var accounts=new List<(string Id,string Username,long Revision)>();
        using(var find=c.CreateCommand())
        {
            find.Transaction=tx;
            find.CommandText="""
                SELECT UserId,Username,RowVersion FROM Users
                WHERE LinkedPersonId=$person AND ActiveFlag=1 ORDER BY UserId;
                """;
            find.Parameters.AddWithValue("$person",personId);
            using var reader=await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while(await reader.ReadAsync(ct).ConfigureAwait(false))
                accounts.Add((reader.GetString(0),reader.GetString(1),reader.GetInt64(2)));
        }
        foreach(var account in accounts)
        {
            using(var update=c.CreateCommand())
            {
                update.Transaction=tx;
                update.CommandText="""
                    UPDATE Users SET ActiveFlag=0,RowVersion=RowVersion+1,
                        LockedUntilUtc=NULL,FailedLoginCount=0
                    WHERE UserId=$id AND LinkedPersonId=$person
                        AND ActiveFlag=1 AND RowVersion=$version;
                    """;
                update.Parameters.AddWithValue("$id",account.Id);
                update.Parameters.AddWithValue("$person",personId);
                update.Parameters.AddWithValue("$version",account.Revision);
                if(await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)
                    throw new InvalidOperationException("Account version changed during personnel exit.");
            }
            using(var sessions=c.CreateCommand())
            {
                sessions.Transaction=tx;
                sessions.CommandText="UPDATE UserSessions SET RevokedFlag=1 WHERE UserId=$id AND RevokedFlag=0;";
                sessions.Parameters.AddWithValue("$id",account.Id);
                await sessions.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await BridgeAuditWriter.WriteAsync(c,tx,clock,"USER_LIFECYCLE_SUSPEND",
                "تعطيل حساب مرتبط بفرد خرج من القوة",
                actorUserId,actorUsername,actorUsername,"User",account.Id,account.Username,
                new JsonObject{["active"]=true,["personId"]=personId}.ToJsonString(),
                new JsonObject{["active"]=false,["personId"]=personId,
                    ["reasonCode"]=reasonCode}.ToJsonString(),
                "تعطيل الحساب وإلغاء جلساته نتيجة "+reasonCode,
                reasonCode,effectiveDate,Guid.NewGuid().ToString("N"),ct).ConfigureAwait(false);
        }
        return accounts.Count;
    }
}
