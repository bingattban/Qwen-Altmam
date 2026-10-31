using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Domain.Policies;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Roster;

/// <summary>
/// Phase 05 service-entry authorization. The native dispatcher authenticates the
/// session nonce, then this independent SQLCipher check verifies the account
/// still exists, is enabled, and has the exact database role (not a UI claim).
/// Internal fixture-seeding APIs remain separate from these production entrypoints.
/// </summary>
public sealed class RosterAuthorizedCommands
{
    private readonly AlTamamDbContext _db;
    private readonly PersonRosterService _roster;
    private readonly ArchivedReturnService _returns;
    private readonly TimeProvider _time;

    public RosterAuthorizedCommands(AlTamamDbContext database, TimeProvider? time=null)
    {
        _db=database;
        _time=time??TimeProvider.System;
        _roster=new PersonRosterService(database,_time);
        _returns=new ArchivedReturnService(database,_time);
    }

    private static BridgeResponse Denied() =>
        new("",false,null,BridgeErrorCodes.Unauthorized,
            "لا تملك صلاحية تنفيذ هذه العملية؛ لم تتغير بيانات القوة.");

    private async Task<bool> Allowed(BridgePrincipal? actor,bool adminOnly,CancellationToken ct)
    {
        if(actor is null || string.IsNullOrWhiteSpace(actor.UserId) ||
           !RoleAuthorizationPolicy.CanPerformStandardOperations(actor.Role) ||
           (adminOnly&&!RoleAuthorizationPolicy.CanArchivePersonsFromActiveForce(actor.Role)))
            return false;
        using var c=_db.CreateOpenConnection();
        using var cmd=c.CreateCommand();
        cmd.CommandText="""
            SELECT u.Role FROM Users u LEFT JOIN Persons p ON p.PersonId=u.LinkedPersonId
            WHERE u.UserId=$actor AND u.ActiveFlag=1 AND
              ((u.Role=1 AND u.LinkedPersonId IS NULL)
               OR (u.Role<>1 AND p.ActiveFlag=1 AND p.OrganizationalStatus=1));
            """;
        cmd.Parameters.AddWithValue("$actor",actor.UserId);
        object? actual=await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return actual is not null and not DBNull &&
               Convert.ToInt32(actual)==(int)actor.Role;
    }

    public async Task<BridgeResponse> CreateAsync(BridgePrincipal? actor,JsonObject? data,CancellationToken ct)
    {
        if(!await Allowed(actor,false,ct).ConfigureAwait(false))return Denied();
        // The external Add Person command must use the CURRENT eligible local
        // date; older dates remain available only to trusted fixture/import paths.
        string provided="";
        try{provided=data?["firstRecordedLocalDate"]?.GetValue<string>()??"";}catch{return Denied();}
        if(provided.Length>0 && provided!=_time.GetLocalNow().ToString("yyyy-MM-dd"))
            return new("",false,null,BridgeErrorCodes.HistoricDayLocked,
                "إضافة فرد جديد تبدأ من اليوم المحلي الحالي، لا من تاريخ سابق أو مستقبلي.");
        return await _roster.CreatePersonAsync(actor!.UserId,actor.Username,
            data??new JsonObject(),ct).ConfigureAwait(false);
    }

    public async Task<BridgeResponse> PreviewReturnAsync(BridgePrincipal? actor,JsonObject? data,CancellationToken ct)
    {
        if(!await Allowed(actor,false,ct).ConfigureAwait(false))return Denied();
        return await _returns.PreviewAsync(data,actor!,ct).ConfigureAwait(false);
    }
    public async Task<BridgeResponse> CommitReturnAsync(BridgePrincipal? actor,JsonObject? data,CancellationToken ct)
    {
        if(!await Allowed(actor,false,ct).ConfigureAwait(false))return Denied();
        return await _returns.CommitReturnAsync(data,actor!,ct).ConfigureAwait(false);
    }
    public async Task<BridgeResponse> PreviewEditAsync(BridgePrincipal? actor,JsonObject? data,CancellationToken ct)
    {
        if(!await Allowed(actor,false,ct).ConfigureAwait(false))return Denied();
        return await _roster.PreviewPersonIdentityUpdateAsync(data??new JsonObject(),ct).ConfigureAwait(false);
    }
    public async Task<BridgeResponse> EditAsync(BridgePrincipal? actor,JsonObject? data,CancellationToken ct)
    {
        if(!await Allowed(actor,false,ct).ConfigureAwait(false))return Denied();
        return await _roster.UpdatePersonIdentityAsync(actor!.UserId,actor.Username,
            data??new JsonObject(),ct).ConfigureAwait(false);
    }
    public async Task<BridgeResponse> PreviewRemovalAsync(BridgePrincipal? actor,JsonObject? data,CancellationToken ct)
    {
        if(!await Allowed(actor,true,ct).ConfigureAwait(false))return Denied();
        return await _roster.PreviewArchivePersonsAsync(data??new JsonObject(),actor!.UserId,ct).ConfigureAwait(false);
    }
    public async Task<BridgeResponse> RemoveAsync(BridgePrincipal? actor,JsonObject? data,CancellationToken ct)
    {
        if(!await Allowed(actor,true,ct).ConfigureAwait(false))return Denied();
        return await _roster.ArchivePersonsAsync(actor!.UserId,actor.Username,
            data??new JsonObject(),ct).ConfigureAwait(false);
    }
}
