using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Infrastructure.Security;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Local-only, GeneralAdmin-only full application data reset. Preview produces
/// a single-use token; commit re-authenticates the founder with their CURRENT
/// password and deletes database records (including audit history and users)
/// in ONE rollback-capable SQLCipher transaction. The schema and protected
/// default statuses/sites are rebuilt from the bundled trusted V001 seed.
/// </summary>
public sealed class FullDataResetService
{
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _clock;
    private const string Operation = "CommitFullReset";
    private const string Phrase = "تصفير جميع البيانات";
    public FullDataResetService(AlTamamDbContext db, TimeProvider? clock=null)
    { _db=db; _clock=clock??TimeProvider.System; }

    private static BridgeResponse Refuse(string code,string message)=>BridgeHelpers.Error(code,message);

    // Full-content fingerprint rather than SUM(RowVersion) or row counts:
    // the latter can falsely match when two records change in opposite ways.
    // PreviewTokens and idle-only UserSessions are excluded because issuing a
    // preview or extending a session must not make that preview stale.
    private static async Task<long> Revision(SqliteConnection c, SqliteTransaction? t, CancellationToken ct)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var tables=new List<string>();
        using(var q=c.CreateCommand())
        {
            q.Transaction=t;
            q.CommandText="""
                SELECT name FROM sqlite_master WHERE type='table'
                AND name NOT LIKE 'sqlite_%'
                AND name NOT IN ('SchemaVersions','PreviewTokens','UserSessions')
                ORDER BY name;
                """;
            using var reader=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while(await reader.ReadAsync(ct).ConfigureAwait(false)) tables.Add(reader.GetString(0));
        }
        foreach(string table in tables)
        {
            Feed(hash,table);
            using var q=c.CreateCommand();
            q.Transaction=t;
            q.CommandText=$"SELECT * FROM \"{table.Replace("\"","\"\"")}\" ORDER BY rowid;";
            using var reader=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while(await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                for(int i=0;i<reader.FieldCount;i++)
                {
                    if(await reader.IsDBNullAsync(i,ct).ConfigureAwait(false))
                        Feed(hash,"<NULL>");
                    else if(reader.GetValue(i) is byte[] bytes)
                    {
                        Feed(hash,"<BLOB>"); Feed(hash,Convert.ToHexString(bytes));
                    }
                    else Feed(hash,Convert.ToString(reader.GetValue(i),
                        System.Globalization.CultureInfo.InvariantCulture)??"");
                }
                Feed(hash,"<ROW>");
            }
        }
        // JSON transports through JavaScript; keep the fingerprint within
        // Number.MAX_SAFE_INTEGER so the confirmation returns the exact value.
        return BitConverter.ToInt64(hash.GetHashAndReset(),0)&((1L<<53)-1);
    }

    private static void Feed(IncrementalHash hash,string text)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(text);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static async Task<long> Count(SqliteConnection c,string table,CancellationToken ct)
    {
        using var q=c.CreateCommand();
        q.CommandText=$"SELECT COUNT(*) FROM \"{table}\";";
        return Convert.ToInt64(await q.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async Task<BridgeResponse> PreviewAsync(BridgePrincipal actor, CancellationToken ct)
    {
        if(actor.Role!=UserRole.GeneralAdministration)
            return Refuse(BridgeErrorCodes.Unauthorized,"تصفير البيانات محصور بالإدارة العامة.");
        using var c=_db.CreateOpenConnection();
        long revision=await Revision(c,null,ct).ConfigureAwait(false);
        long people=await Count(c,"Persons",ct).ConfigureAwait(false);
        long attendance=await Count(c,"AttendanceRecords",ct).ConfigureAwait(false);
        long backups=await Count(c,"BackupHistory",ct).ConfigureAwait(false);
        string token=BridgePreviewTokens.Issue(_db,_clock,Operation,actor.UserId,
            new {people,attendance,backups,revision},revision,ct);
        return BridgeHelpers.Ok(new JsonObject{
            ["previewToken"]=token,["expectedRevision"]=revision,
            ["people"]=people,["attendance"]=attendance,["backupEntries"]=backups,
            ["requiredConfirmation"]=Phrase,
            ["warning"]="سيُحذف جميع الأفراد والتحضير والحسابات والسجلات والعمليات نهائياً، وتعود شاشة التهيئة الأولى. العملية غير قابلة للتراجع."
        });
    }

    private sealed record BackupFile(string Path,string Sha256);

    public async Task<BridgeResponse> CommitAsync(JsonObject? payload,BridgePrincipal actor,CancellationToken ct)
    {
        if(actor.Role!=UserRole.GeneralAdministration)
            return Refuse(BridgeErrorCodes.Unauthorized,"هذه العملية مخصصة للإدارة العامة فقط.");
        string token=payload?["previewToken"]?.GetValue<string>()??"";
        string text=payload?["confirmation"]?.GetValue<string>()??"";
        string password=payload?["password"]?.GetValue<string>()??"";
        long expected;
        try {expected=payload?["expectedRevision"]?.GetValue<long>()??-1;}
        catch {return Refuse(BridgeErrorCodes.ValidationError,"إصدار المعاينة غير صحيح.");}
        if(text!=Phrase || string.IsNullOrWhiteSpace(password) || token.Length!=64)
            return Refuse(BridgeErrorCodes.ValidationError,"أدخل عبارة التأكيد الصحيحة وكلمة مرور الإدارة العامة.");

        var backups=new List<BackupFile>();
        BridgeResponse result=await _db.ExecuteInWriteTransactionAsync(async(c,t)=>
        {
            using(var check=c.CreateCommand())
            {
                check.Transaction=t;
                check.CommandText="SELECT CredentialHash,PasswordSalt,Role FROM Users WHERE UserId=$id AND ActiveFlag=1;";
                check.Parameters.AddWithValue("$id",actor.UserId);
                using var reader=await check.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if(!await reader.ReadAsync(ct).ConfigureAwait(false)
                    || reader.GetInt32(2)!=(int)UserRole.GeneralAdministration
                    || !PasswordSecurity.VerifyPassword(password,reader.GetString(0),reader.GetString(1)))
                    return Refuse(BridgeErrorCodes.Unauthorized,"كلمة المرور الحالية غير صحيحة.");
            }
            long actual=await Revision(c,t,ct).ConfigureAwait(false);
            if(actual!=expected)
                return Refuse(BridgeErrorCodes.ConflictStale,"تغيرت البيانات بعد المعاينة؛ أعد المعاينة قبل التصفير.");
            long? consumption=await BridgePreviewTokens.ConsumeAsync(
                _db,_clock,c,t,token,Operation,actor.UserId,expected,ct).ConfigureAwait(false);
            if(consumption!=expected)
                return Refuse(BridgeErrorCodes.ConflictStale,"معاينة التصفير منتهية أو مستعملة أو غير مرتبطة بالحساب.");

            using(var query=c.CreateCommand())
            {
                query.Transaction=t;
                query.CommandText="SELECT BackupFilePath,Sha256Checksum FROM BackupHistory WHERE VerificationPassed=1;";
                using var reader=await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while(await reader.ReadAsync(ct).ConfigureAwait(false))
                    backups.Add(new BackupFile(reader.GetString(0),reader.GetString(1)));
            }

            var triggers=new List<(string Name,string Sql)>();
            using(var q=c.CreateCommand())
            {
                q.Transaction=t;
                q.CommandText="SELECT name,sql FROM sqlite_master WHERE type='trigger' AND sql IS NOT NULL;";
                using var reader=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while(await reader.ReadAsync(ct).ConfigureAwait(false))
                    triggers.Add((reader.GetString(0),reader.GetString(1)));
            }
            var tables=new List<string>();
            using(var q=c.CreateCommand())
            {
                q.Transaction=t;
                q.CommandText="""
                    SELECT name FROM sqlite_master WHERE type='table'
                      AND name NOT LIKE 'sqlite_%' AND name!='SchemaVersions';
                    """;
                using var reader=await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while(await reader.ReadAsync(ct).ConfigureAwait(false))tables.Add(reader.GetString(0));
            }

            // Transactionally remove protective triggers only for the duration of a
            // password-confirmed FULL reset. Every original trigger is recreated before commit.
            // All tables are deleted with deferred foreign-key checks; no partial reset may commit.
            using(var defer=c.CreateCommand())
            {
                defer.Transaction=t;
                // secure_delete overwrites freed SQLite payload pages instead of
                // leaving old encrypted-but-decryptable records on the freelist.
                defer.CommandText="PRAGMA secure_delete=ON; PRAGMA defer_foreign_keys=ON;";
                await defer.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            foreach(var trigger in triggers)
                await Execute(c,t,$"DROP TRIGGER \"{trigger.Name.Replace("\"","\"\"")}\";",ct).ConfigureAwait(false);
            foreach(string table in tables)
                await Execute(c,t,$"DELETE FROM \"{table.Replace("\"","\"\"")}\";",ct).ConfigureAwait(false);

            var assembly=typeof(AlTamamDbContext).Assembly;
            await using var resource=assembly.GetManifestResourceStream(
                "AlTamam.Infrastructure.Persistence.Migrations.V001_InitialSchema.sql")
                ??throw new InvalidOperationException("Trusted database seed is missing");
            using var file=new StreamReader(resource);
            string sql=await file.ReadToEndAsync(ct).ConfigureAwait(false);
            int start=sql.IndexOf("-- SEED DATA",StringComparison.Ordinal);
            int end=sql.IndexOf("-- Disallow invalid or overlapping",start,StringComparison.Ordinal);
            if(start<0 || end<=start)throw new InvalidOperationException("Seed contract is incomplete");
            await Execute(c,t,sql[start..end],ct).ConfigureAwait(false);
            foreach(var trigger in triggers)
                await Execute(c,t,trigger.Sql,ct).ConfigureAwait(false);
            // Validate referential integrity, even after all original triggers are reinstated.
            using(var fk=c.CreateCommand())
            {
                fk.Transaction=t;
                fk.CommandText="PRAGMA foreign_key_check;";
                if(await fk.ExecuteScalarAsync(ct).ConfigureAwait(false)is not null)
                    throw new InvalidOperationException("Reset did not pass foreign-key integrity");
            }
            return BridgeHelpers.Ok(new JsonObject{["reset"]=true,["requiresFirstSetup"]=true});
        },ct).ConfigureAwait(false);
        if(!result.Success)return result;
        // External backup files cannot participate in SQLite ACID commits.
        // Never remove unrelated files: delete ONLY named files in the verified
        // backup registry whose hash still matches; report any unsafe/failed cleanup.
        int removed=0;int retained=0;
        foreach(var backup in backups)
        {
            try
            {
                string name=Path.GetFileName(backup.Path);
                if(!name.StartsWith("AlTamam-",StringComparison.Ordinal)
                   || !name.EndsWith(".dbbackup",StringComparison.OrdinalIgnoreCase)
                   || !name.Contains("-BKP-",StringComparison.Ordinal)) {retained++;continue;}
                if(!File.Exists(backup.Path))continue;
                using var stream=File.OpenRead(backup.Path);
                string hash=Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if(!string.Equals(hash,backup.Sha256,StringComparison.OrdinalIgnoreCase)){retained++;continue;}
                stream.Close();File.Delete(backup.Path);removed++;
            }
            catch(IOException){retained++;}
            catch(UnauthorizedAccessException){retained++;}
        }
        return BridgeHelpers.Ok(new JsonObject{
            ["reset"]=true,["requiresFirstSetup"]=true,["backupsDeleted"]=removed,
            ["backupFilesNotDeleted"]=retained
        });
    }

    private static async Task Execute(SqliteConnection c,SqliteTransaction t,string sql,CancellationToken ct)
    {
        using var q=c.CreateCommand();q.Transaction=t;q.CommandText=sql;
        await q.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
