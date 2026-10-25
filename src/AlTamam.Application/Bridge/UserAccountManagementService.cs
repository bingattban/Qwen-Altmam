using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Domain.Policies;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Infrastructure.Security;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Server-authorized local user accounts. Stable person links, Argon2id credential
/// hashes, optimistic revisions, audit and session revocation share one transaction.
/// No password, salt or credential hash is returned to WebView2 or written to audit.
/// </summary>
public sealed class UserAccountManagementService
{
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;
    public UserAccountManagementService(AlTamamDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    private static string Read(JsonObject? data, string key)
    {
        try { return data?[key]?.GetValue<string>()?.Trim() ?? ""; }
        catch { return ""; }
    }
    private static string ReadSecret(JsonObject? data)
    {
        try { return data?["password"]?.GetValue<string>() ?? ""; }
        catch { return ""; }
    }
    private static int ReadInt(JsonObject? data, string key, int fallback = 0)
    {
        try { return data?[key]?.GetValue<int>() ?? fallback; }
        catch { return fallback; }
    }
    private static long ReadLong(JsonObject? data, string key)
    {
        try { return data?[key]?.GetValue<long>() ?? 0; }
        catch
        {
            try { return data?[key]?.GetValue<int>() ?? 0; }
            catch { return 0; }
        }
    }
    private static BridgeResponse Error(string explanation, string code = BridgeErrorCodes.ValidationError)
        => BridgeHelpers.Error(code, explanation);

    private async Task Audit(SqliteConnection c, SqliteTransaction t, BridgePrincipal actor,
        string code, string title, string targetId, string targetName, string? before,
        string after, CancellationToken ct)
    {
        await BridgeAuditWriter.WriteAsync(c,t,_time,code,title,actor.UserId,actor.Username,
            actor.Username,"User",targetId,targetName,before,after,title+" — "+targetName,
            null,_time.GetLocalNow().ToString("yyyy-MM-dd"),ct.CorrelationId(),ct).ConfigureAwait(false);
    }

    public async Task<BridgeResponse> ListAsync(JsonObject? payload, BridgePrincipal principal, CancellationToken ct)
    {
        if (!RoleAuthorizationPolicy.CanAccessUsersManagement(principal.Role))
            return Error("لا تملك صلاحية إدارة المستخدمين.",BridgeErrorCodes.Unauthorized);
        int page = Math.Clamp(ReadInt(payload,"page",1),1,100000);
        int size = Math.Clamp(ReadInt(payload,"pageSize",30),1,100);
        string term = Read(payload,"term");
        string pattern = "%" + term.Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_") + "%";
        string where = principal.Role == UserRole.OrdinaryAdministration
            ? "WHERE u.Role = 3 AND (u.Username LIKE $term ESCAPE '\\' OR u.FoundingFullName LIKE $term ESCAPE '\\' OR p.CurrentVisibleName LIKE $term ESCAPE '\\' OR p.CurrentServiceNumberA LIKE $term ESCAPE '\\')"
            : "WHERE (u.Username LIKE $term ESCAPE '\\' OR u.FoundingFullName LIKE $term ESCAPE '\\' OR p.CurrentVisibleName LIKE $term ESCAPE '\\' OR p.CurrentServiceNumberA LIKE $term ESCAPE '\\')";
        using var c=_db.CreateOpenConnection();
        using var count=c.CreateCommand();
        count.CommandText="SELECT COUNT(*) FROM Users u LEFT JOIN Persons p ON p.PersonId=u.LinkedPersonId "+where;
        count.Parameters.AddWithValue("$term",pattern);
        long total=Convert.ToInt64(await count.ExecuteScalarAsync(ct).ConfigureAwait(false));
        using var cmd=c.CreateCommand();
        cmd.CommandText=$"""
            SELECT u.UserId, u.Username, u.Role, u.ActiveFlag, u.RowVersion,
                CASE WHEN u.Role = 1
                     THEN 'الإدارة العامة - ' || u.Username ||
                       CASE WHEN u.FoundingFullName IS NOT NULL AND trim(u.FoundingFullName)<>''
                            THEN ' (' || u.FoundingFullName || ')' ELSE '' END
                     ELSE COALESCE(p.CurrentVisibleName,'غير مرتبط بكشف القوة') END,
                COALESCE(p.CurrentServiceNumberA,''),
                u.LinkedPersonId, u.CreatedByDisplaySnapshot, u.CreatedAtUtc
            FROM Users u LEFT JOIN Persons p ON p.PersonId=u.LinkedPersonId
            {where}
            ORDER BY u.CreatedAtUtc DESC, u.UserId DESC
            LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$term",pattern);
        cmd.Parameters.AddWithValue("$limit",size);
        cmd.Parameters.AddWithValue("$offset",(long)(page-1)*size);
        var rows=new JsonArray();
        using var reader=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while(await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var role=(UserRole)reader.GetInt32(2);
            rows.Add(new JsonObject
            {
                ["index"]=(long)(page-1)*size+rows.Count+1,
                ["userId"]=reader.GetString(0),
                ["username"]=reader.GetString(1),
                ["role"]=role.ToString(),
                ["roleLabelAr"]=role.ToArabicLabel(),
                ["active"]=reader.GetInt32(3)==1,
                ["rowVersion"]=reader.GetInt64(4),
                ["fullName"]=reader.GetString(5),
                ["serviceNumberA"]=reader.GetString(6),
                ["personId"]=reader.IsDBNull(7)?null:reader.GetString(7),
                ["createdBy"]=reader.GetString(8),
                ["createdAtUtc"]=reader.GetString(9),
                ["canModify"]=RoleAuthorizationPolicy.CanModifyAccount(principal.Role,role)
                    && principal.UserId!=reader.GetString(0)
            });
        }
        return BridgeHelpers.Ok(new JsonObject
        {
            ["items"]=rows,["page"]=page,["pageSize"]=size,
            ["total"]=total,["hasNext"]=(long)page*size<total
        });
    }

    public BridgeResponse AllowedRoles(BridgePrincipal actor)
    {
        if(!RoleAuthorizationPolicy.CanAccessUsersManagement(actor.Role))
            return Error("لا تملك صلاحية إدارة المستخدمين.",BridgeErrorCodes.Unauthorized);
        var allowed=new JsonArray();
        foreach(var role in new[]{UserRole.OrdinaryAdministration,UserRole.Supervisor})
            if(RoleAuthorizationPolicy.CanCreateAccountWithRole(actor.Role,role))
                allowed.Add(new JsonObject{["role"]=role.ToString(),["name"]=role.ToArabicLabel()});
        return BridgeHelpers.Ok(allowed);
    }

    public async Task<BridgeResponse> CreateAsync(JsonObject? payload,BridgePrincipal actor,CancellationToken ct)
    {
        string username=Read(payload,"username"), personId=Read(payload,"personId");
        string password=ReadSecret(payload),roleText=Read(payload,"role");
        if(!RoleAuthorizationPolicy.CanAccessUsersManagement(actor.Role))
            return Error("ليست لديك صلاحية إضافة مستخدم.",BridgeErrorCodes.Unauthorized);
        if(!Enum.TryParse<UserRole>(roleText,false,out var role)
            || !RoleAuthorizationPolicy.CanCreateAccountWithRole(actor.Role,role))
            return Error("لا يسمح لك بإنشاء حساب من هذا النوع.",BridgeErrorCodes.Unauthorized);
        if(username.Length is <3 or >64 || username.Any(char.IsWhiteSpace) || personId.Length==0)
            return Error("اسم مستخدم من 3 إلى 64 حرفاً بلا مسافات واختيار فرد مسجل مطلوبان.");
        var policy=PasswordPolicyValidator.Validate(password);
        if(!policy.IsValid)return Error(policy.ArabicErrorMessage??"كلمة المرور غير صالحة.");
        string normalized=BridgeSharedServices.NormalizeUsername(username);
        var (hash,salt)=PasswordSecurity.HashPassword(password);
        string id="USR-"+Guid.NewGuid().ToString("N"),now=_time.GetUtcNow().ToString("O");
        return await _db.ExecuteInWriteTransactionAsync(async(c,t)=>
        {
            var (replay,receipt)=await MutationIdempotency.StartAsync(
                c,t,"CreateUserAccount",actor.UserId,payload,ct).ConfigureAwait(false);
            if(replay is not null)return replay;
            using var person=c.CreateCommand();person.Transaction=t;
            person.CommandText="""
                SELECT CurrentVisibleName,CurrentServiceNumberA FROM Persons
                WHERE PersonId=$person AND ActiveFlag=1 AND OrganizationalStatus=1;
                """;
            person.Parameters.AddWithValue("$person",personId);
            string personName,number;
            using(var reader=await person.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if(!await reader.ReadAsync(ct).ConfigureAwait(false))
                    return Error("يجب اختيار فرد نشط من كشف القوة، وليس إدخال اسم خارجي.");
                personName=reader.GetString(0);number=reader.GetString(1);
            }
            using var duplicate=c.CreateCommand();duplicate.Transaction=t;
            duplicate.CommandText="""
                SELECT COUNT(*) FROM Users
                WHERE NormalizedUsername=$name OR (LinkedPersonId=$person AND ActiveFlag=1);
                """;
            duplicate.Parameters.AddWithValue("$name",normalized);
            duplicate.Parameters.AddWithValue("$person",personId);
            if(Convert.ToInt64(await duplicate.ExecuteScalarAsync(ct).ConfigureAwait(false))!=0)
                return Error("اسم المستخدم مستخدم أو يوجد حساب نشط مرتبط بهذا الفرد.",BridgeErrorCodes.DuplicateUsername);
            // Freeze the creator's real roster identity AT account creation;
            // later name/personnel-number edits must never rewrite this evidence.
            string creatorName=actor.Username;
            using(var creator=c.CreateCommand())
            {
                creator.Transaction=t;
                creator.CommandText="""
                    SELECT p.CurrentVisibleName FROM Users u
                    JOIN Persons p ON p.PersonId=u.LinkedPersonId
                    WHERE u.UserId=$creator;
                    """;
                creator.Parameters.AddWithValue("$creator",actor.UserId);
                string? roster=await creator.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                if(!string.IsNullOrWhiteSpace(roster))
                    creatorName=actor.Username+" - "+roster;
            }
            using var cmd=c.CreateCommand();cmd.Transaction=t;
            cmd.CommandText="""
                INSERT INTO Users(UserId,LinkedPersonId,Username,NormalizedUsername,Role,
                  CredentialHash,PasswordSalt,RequiresFirstRunSetup,ActiveFlag,FailedLoginCount,
                  CreatedByUserId,CreatedByDisplaySnapshot,CreatedAtUtc,RowVersion)
                VALUES($id,$person,$username,$normalized,$role,$hash,$salt,0,1,0,$actor,$actorName,$now,1);
                INSERT INTO UserRoleHistory(UserId,OldRole,NewRole,ChangedByUserId,ChangedAtUtc)
                VALUES($id,NULL,$role,$actor,$now);
                """;
            cmd.Parameters.AddWithValue("$id",id);
            cmd.Parameters.AddWithValue("$person",personId);
            cmd.Parameters.AddWithValue("$username",username);
            cmd.Parameters.AddWithValue("$normalized",normalized);
            cmd.Parameters.AddWithValue("$role",(int)role);
            cmd.Parameters.AddWithValue("$hash",hash);
            cmd.Parameters.AddWithValue("$salt",salt);
            cmd.Parameters.AddWithValue("$actor",actor.UserId);
            cmd.Parameters.AddWithValue("$actorName",creatorName);
            cmd.Parameters.AddWithValue("$now",now);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await Audit(c,t,actor,"USER_CREATE","إضافة حساب",id,username,null,
                new JsonObject{["role"]=role.ToString(),["personId"]=personId,
                    ["name"]=personName,["serviceNumberA"]=number}.ToJsonString(),ct).ConfigureAwait(false);
            var outcome=new JsonObject{["userId"]=id,["username"]=username,
                ["role"]=role.ToString(),["linkedPersonId"]=personId};
            await MutationIdempotency.StoreAsync(c,t,receipt,outcome,_time,ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(outcome);
        },ct).ConfigureAwait(false);
    }

    private async Task<BridgeResponse> ChangeAsync(JsonObject? payload,BridgePrincipal actor,
        bool deactivate,CancellationToken ct)
    {
        string id=Read(payload,"userId");
        long expected=ReadLong(payload,"expectedRevision");
        string password=ReadSecret(payload);
        if(id.Length==0||expected<1)return Error("اختر الحساب وإصداره الحالي.");
        // Ordinary password editing NEVER changes role, login name, identity,
        // linked PersonId, activation status or any other privileged field.
        if(!deactivate && payload is not null &&
           payload.Any(field=>field.Key is not
             ("userId" or "expectedRevision" or "password" or "operationId")))
            return Error("تعديل كلمة المرور لا يسمح بتغيير اسم المستخدم أو الصلاحية أو ربط الفرد.");
        if(deactivate && payload is not null &&
           payload.Any(field=>field.Key is not
             ("userId" or "expectedRevision" or "operationId")))
            return Error("تعطيل الحساب لا يسمح بتعديل بياناته أو صلاحياته.");
        if(id==actor.UserId)return Error("لا يمكن تعديل أو تعطيل الحساب المستخدم حالياً.");
        if(!deactivate)
        {
            var policy=PasswordPolicyValidator.Validate(password);
            if(!policy.IsValid)return Error(policy.ArabicErrorMessage??"كلمة المرور غير صالحة.");
        }
        return await _db.ExecuteInWriteTransactionAsync(async(c,t)=>
        {
            using var select=c.CreateCommand();select.Transaction=t;
            select.CommandText="SELECT Username,Role,ActiveFlag,RowVersion FROM Users WHERE UserId=$id;";
            select.Parameters.AddWithValue("$id",id);
            string username;UserRole role;int active;long revision;
            using(var reader=await select.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if(!await reader.ReadAsync(ct).ConfigureAwait(false))return Error("الحساب غير موجود.");
                username=reader.GetString(0);role=(UserRole)reader.GetInt32(1);
                active=reader.GetInt32(2);revision=reader.GetInt64(3);
            }
            if(!RoleAuthorizationPolicy.CanModifyAccount(actor.Role,role))
                return Error("صلاحية تعديل هذا النوع من الحسابات غير متاحة.",BridgeErrorCodes.Unauthorized);
            // Authorization is revalidated on EVERY retry, while the receipt
            // precedes stale version checks. A lost successful response can be
            // replayed safely even though the first commit incremented RowVersion.
            var (replay,receipt)=await MutationIdempotency.StartAsync(
                c,t,deactivate?"DeactivateUserAccount":"ChangeUserPassword",
                actor.UserId,payload,ct).ConfigureAwait(false);
            if(replay is not null)return replay;
            if(revision!=expected)return Error("بيانات الحساب تغيرت؛ أعد تحديث القائمة.",BridgeErrorCodes.ConflictStale);
            if(active==0)return Error("الحساب معطل بالفعل.");
            if(deactivate)
            {
                if(role==UserRole.GeneralAdministration)
                {
                    using var count=c.CreateCommand();count.Transaction=t;
                    count.CommandText="SELECT COUNT(*) FROM Users WHERE Role=1 AND ActiveFlag=1;";
                    if(Convert.ToInt64(await count.ExecuteScalarAsync(ct).ConfigureAwait(false))<=1)
                        return Error("لا يمكن تعطيل آخر حساب إدارة عامة.");
                }
                using var cmd=c.CreateCommand();cmd.Transaction=t;
                cmd.CommandText="""
                    UPDATE Users SET ActiveFlag=0,RowVersion=RowVersion+1 WHERE UserId=$id AND RowVersion=$rev;
                    UPDATE UserSessions SET RevokedFlag=1 WHERE UserId=$id;
                    """;
                cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$rev",expected);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await Audit(c,t,actor,"USER_DEACTIVATE","تعطيل حساب",id,username,
                    new JsonObject{["active"]=true}.ToJsonString(),
                    new JsonObject{["active"]=false}.ToJsonString(),ct).ConfigureAwait(false);
            }
            else
            {
                var (hash,salt)=PasswordSecurity.HashPassword(password);
                using var cmd=c.CreateCommand();cmd.Transaction=t;
                cmd.CommandText="""
                    UPDATE Users SET CredentialHash=$hash, PasswordSalt=$salt,
                       FailedLoginCount=0,LockedUntilUtc=NULL,RowVersion=RowVersion+1
                    WHERE UserId=$id AND RowVersion=$rev;
                    UPDATE UserSessions SET RevokedFlag=1 WHERE UserId=$id;
                    """;
                cmd.Parameters.AddWithValue("$hash",hash);cmd.Parameters.AddWithValue("$salt",salt);
                cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$rev",expected);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await Audit(c,t,actor,"USER_PASSWORD_CHANGE","تغيير كلمة مرور حساب",id,username,
                    null,new JsonObject{["credentialRotated"]=true}.ToJsonString(),ct).ConfigureAwait(false);
            }
            var outcome=new JsonObject{["userId"]=id,
                ["active"]=!deactivate,["rowVersion"]=expected+1};
            await MutationIdempotency.StoreAsync(c,t,receipt,outcome,_time,ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(outcome);
        },ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 03.13: reactivation NEVER occurs merely because the person returned to
    /// active service. It requires a fresh explicit admin action, optimistic
    /// account version, verified active roster linkage and immutable audit.
    /// Prior user sessions remain permanently revoked.
    /// </summary>
    public async Task<BridgeResponse> ReactivateAsync(JsonObject? payload,
        BridgePrincipal actor,CancellationToken ct)
    {
        string id=Read(payload,"userId"),reason=Read(payload,"reason");
        long expected=ReadLong(payload,"expectedRevision");
        if(id.Length==0 || expected<1 || reason.Length is <5 or >400)
            return Error("اختر حساباً معطّلاً وإصداره الحالي، واكتب سبباً واضحاً (5–400 حرف).");
        return await _db.ExecuteInWriteTransactionAsync(async(c,t)=>
        {
            using var cmd=c.CreateCommand();cmd.Transaction=t;
            cmd.CommandText="""
                SELECT u.Username,u.Role,u.ActiveFlag,u.RowVersion,
                    u.LinkedPersonId,p.ActiveFlag,p.OrganizationalStatus,
                    EXISTS(SELECT 1 FROM PersonAssignments a
                      WHERE a.PersonId=p.PersonId
                        AND a.ValidFromLocalDate<=$today
                        AND (a.ValidToLocalDate IS NULL OR a.ValidToLocalDate>$today))
                FROM Users u LEFT JOIN Persons p ON p.PersonId=u.LinkedPersonId
                WHERE u.UserId=$id;
                """;
            cmd.Parameters.AddWithValue("$id",id);
            cmd.Parameters.AddWithValue("$today",_time.GetLocalNow().ToString("yyyy-MM-dd"));
            string username,personId;UserRole targetRole;long revision;int active;
            using(var reader=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if(!await reader.ReadAsync(ct).ConfigureAwait(false))
                    return Error("الحساب المطلوب غير موجود.");
                username=reader.GetString(0);
                targetRole=(UserRole)reader.GetInt32(1);
                active=reader.GetInt32(2);
                revision=reader.GetInt64(3);
                personId=reader.IsDBNull(4)?"":reader.GetString(4);
                if(string.IsNullOrWhiteSpace(personId) || reader.IsDBNull(5) ||
                   reader.GetInt32(5)!=1 || reader.GetInt32(6)!=1 ||
                   reader.GetInt32(7)!=1)
                    return Error("إعادة تفعيل الحساب تتطلب عودة صاحبه فعلياً إلى كشف القوة النشطة.",
                        BridgeErrorCodes.ConflictStale);
            }
            if(!RoleAuthorizationPolicy.CanModifyAccount(actor.Role,targetRole))
                return Error("لا تملك صلاحية إعادة تفعيل هذا النوع من الحسابات.",
                    BridgeErrorCodes.Unauthorized);
            var (replay,receipt)=await MutationIdempotency.StartAsync(
                c,t,"ReactivateUserAccount",actor.UserId,payload,ct)
                .ConfigureAwait(false);
            if(replay is not null)return replay;
            if(active!=0 || revision!=expected)
                return Error("الحساب مفعل أو تغير إصداره؛ أعد تحميل القائمة.",
                    BridgeErrorCodes.ConflictStale);
            using(var duplicate=c.CreateCommand())
            {
                duplicate.Transaction=t;
                duplicate.CommandText="""
                    SELECT EXISTS(SELECT 1 FROM Users
                      WHERE LinkedPersonId=$person AND ActiveFlag=1 AND UserId<>$id);
                    """;
                duplicate.Parameters.AddWithValue("$person",personId);
                duplicate.Parameters.AddWithValue("$id",id);
                if(Convert.ToInt32(await duplicate.ExecuteScalarAsync(ct).ConfigureAwait(false))!=0)
                    return Error("الفرد مرتبط بحساب آخر نشط.",BridgeErrorCodes.ConflictStale);
            }
            using(var update=c.CreateCommand())
            {
                update.Transaction=t;
                update.CommandText="""
                    UPDATE Users SET ActiveFlag=1,RowVersion=RowVersion+1,
                       FailedLoginCount=0,LockedUntilUtc=NULL
                    WHERE UserId=$id AND ActiveFlag=0 AND RowVersion=$expected;
                    """;
                update.Parameters.AddWithValue("$id",id);
                update.Parameters.AddWithValue("$expected",expected);
                if(await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)
                    return Error("تغير الحساب أثناء إعادة التفعيل.",BridgeErrorCodes.ConflictStale);
            }
            await Audit(c,t,actor,"USER_REACTIVATE","إعادة تفعيل حساب",id,username,
                new JsonObject{["active"]=false}.ToJsonString(),
                new JsonObject{["active"]=true,["reason"]=reason,
                    ["personId"]=personId}.ToJsonString(),ct).ConfigureAwait(false);
            var outcome=new JsonObject{
                ["userId"]=id,["active"]=true,["rowVersion"]=expected+1
            };
            await MutationIdempotency.StoreAsync(c,t,receipt,outcome,_time,ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(outcome);
        },ct).ConfigureAwait(false);
    }

    public Task<BridgeResponse> ChangePasswordAsync(JsonObject? payload,BridgePrincipal actor,CancellationToken ct)
        => ChangeAsync(payload,actor,false,ct);

    public Task<BridgeResponse> DeactivateAsync(JsonObject? payload,BridgePrincipal actor,CancellationToken ct)
        => ChangeAsync(payload,actor,true,ct);
}
