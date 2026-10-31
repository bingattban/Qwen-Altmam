using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Contracts.Events;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Domain.Policies;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// SECTION 04.03: identify a possible return before admitting one person.
/// An archived person's permanent PersonId is reused only after a named, actor-bound
/// one-time preview and explicit confirmation. Never infer sameness from a name alone.
/// </summary>
public sealed class ArchivedReturnService
{
    internal const string PreviewOperation = "ResolveArchivedIdentity";
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;

    public ArchivedReturnService(AlTamamDbContext db, TimeProvider? time=null)
    {
        _db=db;
        _time=time??TimeProvider.System;
    }

    private string Today() => _time.GetLocalNow().ToString("yyyy-MM-dd");
    private static BridgeResponse Fail(string message,string code=BridgeErrorCodes.ConflictStale)
        => BridgeHelpers.Error(code,message);
    private static string Text(JsonObject? data,string key)
    {
        try {return data?[key]?.GetValue<string>()?.Trim()??"";}
        catch {return "";}
    }

    /// <summary>Find ALL archived name/number matches and issue a preview token for explicit identity resolution.</summary>
    public async Task<BridgeResponse> PreviewAsync(JsonObject? data,BridgePrincipal actor,CancellationToken ct)
    {
        string name=Text(data,"visibleName"),number=Text(data,"serviceNumberA");
        string site=Text(data,"siteId");
        if(site.Length==0)site="SITE-UNSPECIFIED";
        if(name.Length<3||name.Length>200||
            !ServiceNumberPolicy.TryCanonicalize(number,out number))
            return Fail("أدخل الاسم الكامل والرقم (ع) الصحيح قبل البحث عن هوية مؤرشفة.",
                BridgeErrorCodes.ValidationError);
        string normalized=ArabicTextNormalizer.NormalizeForSearch(name),date=Today();
        using var c=_db.CreateOpenConnection();
        using(var check=c.CreateCommand())
        {
            check.CommandText="SELECT EXISTS(SELECT 1 FROM Sites WHERE SiteId=$site AND ActiveFlag=1);";
            check.Parameters.AddWithValue("$site",site);
            if(Convert.ToInt32(await check.ExecuteScalarAsync(ct).ConfigureAwait(false))!=1)
                return Fail("الموقع المختار غير موجود أو غير نشط.",BridgeErrorCodes.ValidationError);
        }
        using(var check=c.CreateCommand())
        {
            check.CommandText="""
                SELECT PersonId,CurrentVisibleName FROM Persons
                WHERE ActiveFlag=1 AND NormalizedServiceNumberA=$num LIMIT 1;
                """;
            check.Parameters.AddWithValue("$num",number);
            using var conflict=await check.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if(await conflict.ReadAsync(ct).ConfigureAwait(false))
                return Fail($"«الرقم (ع)» مستخدم بالفعل للفرد النشط «{conflict.GetString(1)}» (PersonId: {conflict.GetString(0)}). لا يمكن إنشاء فرد جديد أو استعادة مؤرشف بهذا الرقم.",
                    BridgeErrorCodes.DuplicateServiceNumber);
        }

        var matches=new JsonArray();
        using(var cmd=c.CreateCommand())
        {
            cmd.CommandText="""
                SELECT p.PersonId,p.CurrentVisibleName,p.CurrentServiceNumberA,p.RowVersion,
                       p.OrganizationalStatus,
                       COALESCE((SELECT m.EffectiveLocalDate FROM PersonnelMovements m
                           WHERE m.PersonId=p.PersonId AND m.MovementType IN (2,3)
                           ORDER BY m.EffectiveLocalDate DESC,m.CommittedAtUtc DESC LIMIT 1),
                           (SELECT MAX(ValidToLocalDate) FROM PersonAssignments
                            WHERE PersonId=p.PersonId),'غير موثق'),
                       CASE WHEN p.NormalizedServiceNumberA=$number THEN 1 ELSE 0 END,
                       CASE WHEN p.NormalizedName=$name THEN 1 ELSE 0 END,
                       COALESCE((SELECT m.MovementType FROM PersonnelMovements m
                           WHERE m.PersonId=p.PersonId AND m.MovementType IN(2,3)
                           ORDER BY m.EffectiveLocalDate DESC,m.CommittedAtUtc DESC LIMIT 1),0),
                       COALESCE((SELECT m.SourceSiteNameSnapshot FROM PersonnelMovements m
                           WHERE m.PersonId=p.PersonId AND m.MovementType IN(2,3)
                           ORDER BY m.EffectiveLocalDate DESC,m.CommittedAtUtc DESC LIMIT 1),
                           (SELECT COALESCE(
                               (SELECT hn.VisibleNameSnapshot FROM SiteNameHistory hn
                                WHERE hn.SiteId=a.SiteId AND
                                  hn.ValidFromLocalDate<=DATE(COALESCE(a.ValidToLocalDate,$date),'-1 day')
                                ORDER BY hn.ValidFromLocalDate DESC LIMIT 1),
                               CASE WHEN a.SiteId='SITE-UNSPECIFIED' THEN 'غير محدد' END)
                            FROM PersonAssignments a WHERE a.PersonId=p.PersonId
                            ORDER BY a.ValidFromLocalDate DESC LIMIT 1),
                           'غير موثق')
                FROM Persons p
                WHERE p.ActiveFlag=0 AND p.OrganizationalStatus IN(2,3,4)
                  AND (p.NormalizedServiceNumberA=$number OR p.NormalizedName=$name)
                ORDER BY p.PersonId LIMIT 51;
                """;
            cmd.Parameters.AddWithValue("$number",number);
            cmd.Parameters.AddWithValue("$name",normalized);
            cmd.Parameters.AddWithValue("$date",date);
            using var reader=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while(await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if(matches.Count==50)
                    return Fail("عدد الهويات المحتملة كبير؛ يجب تسوية التعارض في الأرشيف قبل إنشاء فرد جديد.");
                int exitType=reader.GetInt32(8);
                matches.Add(new JsonObject
                {
                    ["personId"]=reader.GetString(0),
                    ["name"]=reader.GetString(1),
                    ["serviceNumberA"]=reader.GetString(2),
                    ["revision"]=reader.GetInt64(3),
                    ["organizationalStatus"]=reader.GetInt32(4),
                    ["exitDate"]=reader.GetString(5),
                    ["previousSiteName"]=reader.GetString(9),
                    ["exitType"]=exitType==2?"نقل خارجي":
                                 exitType==3?"فصل":"أرشيف القوة",
                    ["numberMatch"]=reader.GetInt32(6)!=0,
                    ["nameMatch"]=reader.GetInt32(7)!=0
                });
            }
        }

        if(matches.Count==0)
            return BridgeHelpers.Ok(new JsonObject {["candidates"]=new JsonArray(),
                ["requiresResolution"]=false,["businessDate"]=date});

        var preview=new JsonObject
        {
            ["visibleName"]=name,["serviceNumberA"]=number,
            ["siteId"]=site,["businessDate"]=date,
            ["candidates"]=matches.DeepClone()
        };
        string token=BridgePreviewTokens.Issue(_db,_time,PreviewOperation,
            actor.UserId,preview,0,ct);
        return BridgeHelpers.Ok(new JsonObject
        {
            ["candidates"]=matches,["requiresResolution"]=true,
            ["businessDate"]=date,["previewToken"]=token,
            ["numberContested"]=matches.Any(p=>p?["numberMatch"]?.GetValue<bool>()==true)
        });
    }

    /// <summary>Reactivates the explicitly selected original identity and appends a Return Movement.</summary>
    public async Task<BridgeResponse> CommitReturnAsync(JsonObject? data,BridgePrincipal actor,CancellationToken ct)
    {
        string token=Text(data,"previewToken"),personId=Text(data,"personId");
        if(token.Length!=64||personId.Length==0||Text(data,"decision")!="same")
            return Fail("العودة تتطلب تحديد هوية مؤرشفة وموافقة صريحة بعد المعاينة.",
                BridgeErrorCodes.ValidationError);
        return await _db.ExecuteInWriteTransactionAsync(async(c,t)=>
        {
            var (replay,receipt)=await MutationIdempotency.StartAsync(c,t,
                "CommitArchivedReturn",actor.UserId,data,ct).ConfigureAwait(false);
            if(replay is not null)return replay;

            string? json;
            using(var find=c.CreateCommand())
            {
                find.Transaction=t;
                find.CommandText="""
                    SELECT PayloadCanonicalJson FROM PreviewTokens
                    WHERE Token=$token AND Operation=$op AND ActorUserId=$actor
                      AND ConsumedFlag=0 AND ExpiresAtUtc>$now;
                    """;
                find.Parameters.AddWithValue("$token",token);
                find.Parameters.AddWithValue("$op",PreviewOperation);
                find.Parameters.AddWithValue("$actor",actor.UserId);
                find.Parameters.AddWithValue("$now",_time.GetUtcNow().ToString("O"));
                json=await find.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            }
            if(json is null)return Fail("انتهت معاينة هوية الفرد أو استُخدمت مسبقاً؛ أعد البحث.");
            var plan=JsonNode.Parse(json) as JsonObject;
            if(plan is null||Text(plan,"businessDate")!=Today()||plan["candidates"] is not JsonArray candidates)
                return Fail("انتهت صلاحية تاريخ المعاينة؛ أعد البحث.");
            JsonObject? selected=candidates.OfType<JsonObject>().FirstOrDefault(x=>Text(x,"personId")==personId);
            if(selected is null)return Fail("الهوية المحددة غير موجودة ضمن المرشحين الذين تمت معاينتهم.");
            string name=Text(plan,"visibleName"),number=Text(plan,"serviceNumberA"),site=Text(plan,"siteId");
            long expected=selected["revision"]?.GetValue<long>()??-1;
            int oldOrg=selected["organizationalStatus"]?.GetValue<int>()??-1;
            string oldName=Text(selected,"name"),oldNumber=Text(selected,"serviceNumberA");
            if(expected<1||oldOrg is not(2 or 3 or 4))
                return Fail("الهوية المؤرشفة غير صالحة للاسترجاع.");
            string sourceSite="",sourceName="",lastExit="";
            using(var cur=c.CreateCommand())
            {
                cur.Transaction=t;
                cur.CommandText="""
                    SELECT p.CurrentVisibleName,p.CurrentServiceNumberA,p.ActiveFlag,
                           p.OrganizationalStatus,p.RowVersion,a.SiteId,s.VisibleName,a.ValidToLocalDate
                    FROM Persons p
                    LEFT JOIN PersonAssignments a ON a.AssignmentId=(
                        SELECT a2.AssignmentId FROM PersonAssignments a2
                        WHERE a2.PersonId=p.PersonId
                        ORDER BY a2.ValidFromLocalDate DESC LIMIT 1)
                    LEFT JOIN Sites s ON s.SiteId=a.SiteId
                    WHERE p.PersonId=$id;
                    """;
                cur.Parameters.AddWithValue("$id",personId);
                using var reader=await cur.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if(!await reader.ReadAsync(ct).ConfigureAwait(false))
                    return Fail("الفرد المؤرشف لم يعد موجوداً في قاعدة البيانات.");
                if(reader.GetInt32(2)!=0 ||reader.GetInt32(3)!=oldOrg||
                   reader.GetInt64(4)!=expected||reader.GetString(0)!=oldName||
                   reader.GetString(1)!=oldNumber)
                    return Fail("تغيّرت هوية الفرد المؤرشف بعد المعاينة؛ حدّث البيانات.");
                if(reader.IsDBNull(5)||reader.IsDBNull(7))
                    return Fail("لا توجد فترة انتساب مغلقة مثبتة للفرد المؤرشف.");
                sourceSite=reader.GetString(5);
                sourceName=reader.GetString(6);
                lastExit=reader.GetString(7);
            }
            string date=Today();
            if(string.CompareOrdinal(lastExit,date)>=0)
                return Fail("تاريخ العودة يجب أن يأتي بعد يوم الخروج السابق؛ يمنع تداخل الحركات في يوم واحد.");
            using(var locked=c.CreateCommand())
            {
                locked.Transaction=t;
                locked.CommandText="""
                    SELECT EXISTS(SELECT 1 FROM PeriodClosures WHERE IsClosed=1
                        AND ((PeriodType='DAY' AND PeriodKey=$date)
                          OR (PeriodType='MONTH' AND PeriodKey=$month)));
                    """;
                locked.Parameters.AddWithValue("$date",date);
                locked.Parameters.AddWithValue("$month",date[..7]);
                if(Convert.ToInt32(await locked.ExecuteScalarAsync(ct).ConfigureAwait(false))!=0)
                    return Fail("هذا اليوم مغلق؛ عودة الفرد تحتاج الإجراء الإداري المصرح به.");
            }
            using(var check=c.CreateCommand())
            {
                check.Transaction=t;
                check.CommandText="""
                    SELECT EXISTS(SELECT 1 FROM Sites WHERE SiteId=$site AND ActiveFlag=1),
                           EXISTS(SELECT 1 FROM Persons WHERE PersonId<>$id
                               AND ActiveFlag=1 AND NormalizedServiceNumberA=$number),
                           EXISTS(SELECT 1 FROM Persons WHERE PersonId<>$id
                               AND ActiveFlag=0 AND OrganizationalStatus IN(2,3,4)
                               AND NormalizedServiceNumberA=$number),
                           EXISTS(SELECT 1 FROM PersonnelMovements WHERE PersonId=$id AND EffectiveLocalDate=$date),
                           EXISTS(SELECT 1 FROM PersonAssignments WHERE PersonId=$id
                               AND ValidFromLocalDate<=$date
                               AND (ValidToLocalDate IS NULL OR ValidToLocalDate>$date));
                    """;
                check.Parameters.AddWithValue("$site",site);
                check.Parameters.AddWithValue("$id",personId);
                check.Parameters.AddWithValue("$number",number);
                check.Parameters.AddWithValue("$date",date);
                using var rd=await check.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await rd.ReadAsync(ct).ConfigureAwait(false);
                if(rd.GetInt32(0)!=1)return Fail("موقع العودة غير موجود أو غير نشط.");
                if(rd.GetInt32(1)!=0)return Fail("الرقم (ع) أصبح مستخدماً لفرد نشط.",BridgeErrorCodes.DuplicateServiceNumber);
                if(rd.GetInt32(2)!=0)return Fail("الرقم (ع) متنازع عليه مع هوية أخرى في الأرشيف؛ يجب تسوية الهوية أولاً.");
                if(rd.GetInt32(3)!=0||rd.GetInt32(4)!=0)
                    return Fail("توجد حركة أو فترة انتساب تتداخل مع تاريخ العودة.");
            }

            // A newly issued number or name is an explicit temporal identity change,
            // never an edit to attendance/exit records from earlier days.
            bool changed=name!=oldName||number!=oldNumber;
            if(changed)
            {
                using var history=c.CreateCommand();
                history.Transaction=t;
                history.CommandText="""
                    SELECT ValidFromLocalDate FROM PersonIdentityHistory
                    WHERE PersonId=$id AND ValidToLocalDate IS NULL
                    ORDER BY ValidFromLocalDate DESC LIMIT 1;
                    """;
                history.Parameters.AddWithValue("$id",personId);
                string? from=await history.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                if(from is null||string.CompareOrdinal(from,date)>=0)
                    return Fail("فترة هوية الفرد لا تسمح بتغيير الاسم أو الرقم يوم العودة؛ يلزم تصحيح تعارض التاريخ.");
            }
            long? consumed=await BridgePreviewTokens.ConsumeAsync(_db,_time,c,t,token,
                PreviewOperation,actor.UserId,0,ct).ConfigureAwait(false);
            if(consumed!=0)return Fail("المعاينة منتهية أو مستخدمة مسبقاً.");
            string now=_time.GetUtcNow().ToString("O");
            string moveId="MOV-"+Guid.NewGuid().ToString("N"),assignmentId="ASG-"+Guid.NewGuid().ToString("N");
            using(var update=c.CreateCommand())
            {
                update.Transaction=t;
                update.CommandText="""
                    UPDATE Persons SET OrganizationalStatus=1,ActiveFlag=1,RowVersion=RowVersion+1,
                        CurrentVisibleName=$name,NormalizedName=$normalizedName,
                        CurrentServiceNumberA=$number,NormalizedServiceNumberA=$number
                    WHERE PersonId=$id AND RowVersion=$revision AND ActiveFlag=0
                      AND OrganizationalStatus IN (2,3,4);
                    """;
                update.Parameters.AddWithValue("$id",personId);
                update.Parameters.AddWithValue("$revision",expected);
                update.Parameters.AddWithValue("$name",name);
                update.Parameters.AddWithValue("$number",number);
                update.Parameters.AddWithValue("$normalizedName",ArabicTextNormalizer.NormalizeForSearch(name));
                if(await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)
                    throw new InvalidOperationException("Stale archived return identity.");
            }
            if(changed)
            {
                using var history=c.CreateCommand();
                history.Transaction=t;
                history.CommandText="""
                    UPDATE PersonIdentityHistory SET ValidToLocalDate=$date
                    WHERE PersonId=$id AND ValidToLocalDate IS NULL;
                    INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                        ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
                    VALUES($id,$name,$number,$date,NULL,$actor,$now,'عودة فرد مؤرشف بعد تحقق الهوية');
                    """;
                history.Parameters.AddWithValue("$id",personId);
                history.Parameters.AddWithValue("$name",name);
                history.Parameters.AddWithValue("$number",number);
                history.Parameters.AddWithValue("$date",date);
                history.Parameters.AddWithValue("$actor",actor.UserId);
                history.Parameters.AddWithValue("$now",now);
                await history.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            using(var insert=c.CreateCommand())
            {
                insert.Transaction=t;
                insert.CommandText="""
                    INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                        ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                    VALUES($assignment,$id,$site,$date,NULL,$movement,$now);
                    INSERT INTO PersonnelMovements(MovementId,PersonId,PersonNameSnapshot,
                        ServiceNumberASnapshot,MovementType,SourceSiteId,SourceSiteNameSnapshot,
                        DestinationSiteId,DestinationSiteNameSnapshot,DestinationExternalName,
                        SeparationReason,EffectiveLocalDate,CommittedAtUtc,ActorUserId,IdempotencyKey)
                    VALUES($movement,$id,$name,$number,4,$source,$sourceName,
                        $site,$siteName,NULL,NULL,$date,$now,$actor,$key);
                    """;
                insert.Parameters.AddWithValue("$assignment",assignmentId);
                insert.Parameters.AddWithValue("$movement",moveId);
                insert.Parameters.AddWithValue("$id",personId);
                insert.Parameters.AddWithValue("$name",name);
                insert.Parameters.AddWithValue("$number",number);
                insert.Parameters.AddWithValue("$source",sourceSite);
                insert.Parameters.AddWithValue("$sourceName",sourceName);
                insert.Parameters.AddWithValue("$site",site);
                insert.Parameters.AddWithValue("$date",date);
                insert.Parameters.AddWithValue("$now",now);
                insert.Parameters.AddWithValue("$actor",actor.UserId);
                insert.Parameters.AddWithValue("$key",token+":"+personId);
                using var lookup=c.CreateCommand();lookup.Transaction=t;
                lookup.CommandText="SELECT VisibleName FROM Sites WHERE SiteId=$site AND ActiveFlag=1;";
                lookup.Parameters.AddWithValue("$site",site);
                string siteName=(await lookup.ExecuteScalarAsync(ct).ConfigureAwait(false) as string)!;
                insert.Parameters.AddWithValue("$siteName",siteName);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await BridgeAuditWriter.WriteAsync(c,t,_time,
                "PERSON_RETURN","عودة فرد مؤرشف بعد التحقق من الهوية",
                actor.UserId,actor.Username,actor.Username,"Person",personId,name,
                new JsonObject{["active"]=false,["status"]=oldOrg,
                    ["name"]=oldName,["serviceNumberA"]=oldNumber}.ToJsonString(),
                new JsonObject{["active"]=true,["name"]=name,["serviceNumberA"]=number,
                    ["assignmentId"]=assignmentId,["movementId"]=moveId,["siteId"]=site,
                    ["verifiedSameHuman"]=true}.ToJsonString(),
                "عودة الشخص نفسه إلى القوة مع الحفاظ على معرفه وتاريخه السابق.",
                "إثبات عودة نفس الفرد بعد مراجعة الهوية",date,
                Guid.NewGuid().ToString("N"),ct).ConfigureAwait(false);
            var result=new JsonObject{["personId"]=personId,["returned"]=true,
                ["revision"]=expected+1,["movementId"]=moveId,["siteId"]=site,
                ["businessDate"]=date};
            await MutationIdempotency.StoreAsync(c,t,receipt,result,_time,ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(result,
                events:BridgeHelpers.InvalidationEvent(UiInvalidationEvents.PersonnelArchived,date,[personId]));
        },ct).ConfigureAwait(false);
    }
}
