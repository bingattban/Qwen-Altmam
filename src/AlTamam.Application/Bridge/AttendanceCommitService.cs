using System.Globalization;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Application.Attendance;
using AlTamam.Contracts.Events;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Today's individual/bulk attendance, mandatory preview and one-time token.
/// Every person is revalidated and committed atomically with immutable audit.
/// Historical/closed days, transfer days and absent assignments are rejected.
/// </summary>
public sealed class AttendanceCommitService
{
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;
    public AttendanceCommitService(AlTamamDbContext db,TimeProvider? time=null)
    { _db=db;_time=time??TimeProvider.System; }

    private static string Read(JsonObject? p,string key)
    { try{return p?[key]?.GetValue<string>()?.Trim()??"";}catch{return "";} }
    private string Today()=>_time.GetLocalNow().ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    private bool CutoffReached()
    {
        using var c=_db.CreateOpenConnection();
        using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT SettingValue FROM AppSettings WHERE SettingKey=$key;";
        cmd.Parameters.AddWithValue("$key",AutoAttendancePolicyService.LockTimeKey);
        var selected=cmd.ExecuteScalar() as string;
        var cutoff=AutoAttendancePolicyService.IsValidClock(selected)
            ? TimeOnly.ParseExact(selected!,"HH:mm",CultureInfo.InvariantCulture).ToTimeSpan()
            : TimeSpan.FromHours(20);
        return _time.GetLocalNow().TimeOfDay>=cutoff;
    }
    private static BridgeResponse Fail(string code,string message,string? field=null)=>
        BridgeHelpers.Error(code,message,field);
    private static string[] PersonIds(JsonObject? data)
    {
        if(data?["personIds"] is not JsonArray a)return [];
        try
        {
            var ids=a.Select(n=>n?.GetValue<string>()?.Trim()??"").ToArray();
            if(ids.Length is <1 or >100 || ids.Any(string.IsNullOrWhiteSpace)
                || ids.Distinct(StringComparer.Ordinal).Count()!=ids.Length)return [];
            return ids;
        }
        catch{return [];}
    }
    private record Snapshot(string PersonId,string Name,string Number,
        string AssignmentId,string SiteId,string SiteName,string? PreviousStatus,
        string PreviousName,long PreviousRevision);

    private static async Task<bool> Closed(SqliteConnection c,SqliteTransaction? t,string date,CancellationToken ct)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=t;
        cmd.CommandText="""
            SELECT EXISTS(SELECT 1 FROM PeriodClosures WHERE IsClosed=1
                AND ((PeriodType='DAY' AND PeriodKey=$day)
                     OR (PeriodType='MONTH' AND PeriodKey=$month)));
            """;
        cmd.Parameters.AddWithValue("$day",date);
        cmd.Parameters.AddWithValue("$month",date[..7]);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!=0;
    }
    private static async Task<(string Name,string Symbol,string Color)?> Status(
        SqliteConnection c,SqliteTransaction? t,string id,CancellationToken ct)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=t;
        cmd.CommandText="""
            SELECT VisibleName,ShortSymbol,ChosenColorHex
            FROM AttendanceStatusDefinitions WHERE StatusId=$id AND ActiveFlag=1;
            """;
        cmd.Parameters.AddWithValue("$id",id);
        using var r=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false)
            ?(r.GetString(0),r.GetString(1),r.GetString(2)):null;
    }
    private static async Task<Snapshot?> GetSnapshot(SqliteConnection c,SqliteTransaction? tx,
        string personId,string date,CancellationToken ct)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            SELECT p.CurrentVisibleName,p.CurrentServiceNumberA,
                   a.AssignmentId,a.SiteId,s.VisibleName,
                   ar.StatusId,ar.StatusNameSnapshot,ar.RevisionNumber
            FROM Persons p
            JOIN PersonAssignments a ON a.PersonId=p.PersonId
                AND a.ValidFromLocalDate<=$date
                AND (a.ValidToLocalDate IS NULL OR a.ValidToLocalDate>$date)
            JOIN Sites s ON s.SiteId=a.SiteId
            LEFT JOIN AttendanceRecords ar ON ar.PersonId=p.PersonId
                AND ar.AttendanceLocalDate=$date
            WHERE p.PersonId=$id AND p.ActiveFlag=1 AND p.OrganizationalStatus=1
                AND p.FirstRecordedLocalDate<=$date
                AND NOT EXISTS(SELECT 1 FROM PersonnelMovements m
                    WHERE m.PersonId=p.PersonId AND m.EffectiveLocalDate=$date
                       AND m.MovementType IN(1,2,3));
            """;
        cmd.Parameters.AddWithValue("$date",date);
        cmd.Parameters.AddWithValue("$id",personId);
        using var r=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if(!await r.ReadAsync(ct).ConfigureAwait(false))return null;
        var snap=new Snapshot(personId,r.GetString(0),r.GetString(1),
            r.GetString(2),r.GetString(3),r.GetString(4),
            r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?"غير محضر":r.GetString(6),
            r.IsDBNull(7)?0:r.GetInt64(7));
        if(await r.ReadAsync(ct).ConfigureAwait(false))
            return null; // ambiguous/overlapping assignment, never guess
        return snap;
    }
    private static string PersonLabel(string name,string number) =>
        "«"+name+"» (الرقم (ع): "+number+")";

    // Resolve a rejected person's identity from SQLCipher itself even when the
    // active assignment/status snapshot cannot be formed (transfer, inactivity).
    private static async Task<string> PersonLabelAsync(SqliteConnection c,SqliteTransaction? tx,
        string personId,CancellationToken ct)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT CurrentVisibleName,CurrentServiceNumberA FROM Persons WHERE PersonId=$id;";
        cmd.Parameters.AddWithValue("$id",personId);
        using var r=await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if(await r.ReadAsync(ct).ConfigureAwait(false))
            return PersonLabel(r.GetString(0),r.GetString(1));
        return "الفرد غير الموجود في الكشف";
    }

    private static JsonObject ToJson(Snapshot s)=>new()
    {
        ["personId"]=s.PersonId,["name"]=s.Name,["serviceNumberA"]=s.Number,
        ["assignmentId"]=s.AssignmentId,["siteId"]=s.SiteId,
        ["siteName"]=s.SiteName,["previousStatusId"]=s.PreviousStatus,
        ["previousStatusName"]=s.PreviousName,["previousRevision"]=s.PreviousRevision
    };

    public async Task<BridgeResponse> PreviewAsync(JsonObject? payload,BridgePrincipal actor,
        CancellationToken ct)
    {
        string date=Read(payload,"businessDate"), statusId=Read(payload,"statusId");
        string siteFilter=Read(payload,"siteId"),mode=Read(payload,"mode");
        string reason=Read(payload,"reason");
        var ids=PersonIds(payload);
        if(!AttendanceDateRangePolicy.TryParse(payload,statusId,Today(),out var rangeStart,out var rangeEnd,out var rangeError))
            return Fail(BridgeErrorCodes.ValidationError,rangeError,"rangeStartDate");
        if(date!=Today())return Fail(BridgeErrorCodes.HistoricDayLocked,
            "يسمح التحضير من هذه الشاشة لليوم الحالي فقط، وليس للتواريخ السابقة أو القادمة.",
            "businessDate");
        if(CutoffReached())return Fail(BridgeErrorCodes.HistoricDayLocked,
            "انتهى الوقت المحدد للتحضير اليومي في الإعدادات.");
        var invalidFields=new JsonObject();
        if(ids.Length==0)invalidFields["personIds"]="اختر من فرد واحد إلى 100 فرد دون تكرار.";
        if(statusId.Length==0)invalidFields["statusId"]="اختر الحالة المطلوبة.";
        if(mode is not("initial" or "revise"))
            invalidFields["mode"]="نوع عملية التحضير غير صالح.";
        if(invalidFields.Count>0)
            return Fail(BridgeErrorCodes.ValidationError,
                "اختر الحالة وفرداً أو أكثر (حتى 100) ونوع العملية.")
                with {ValidationErrors=invalidFields};
        // A routine same-day status change requires an explicit, user-bound preview
        // and approval, but the operator must not be forced to type a reason.
        // Preserve an audit explanation even when the UI supplies no reason.
        if(mode=="revise" && reason.Length==0)
            reason="تغيير حالة التمام بتأكيد المستخدم";
        if(mode=="revise" && reason.Length is <5 or >400)
            return Fail(BridgeErrorCodes.ValidationError,"سبب التعديل المدخل غير صالح.","reason");
        using var c=_db.CreateOpenConnection();
        if(await Closed(c,null,date,ct).ConfigureAwait(false))
        {
            if(ids.Length==0)return Fail(BridgeErrorCodes.HistoricDayLocked,
                "اليوم أو الشهر مغلق، ولا يسمح بتعديل تحضيره.");
            var blocked=new JsonArray();
            foreach(var id in ids)
                blocked.Add(await AttendanceConflictDetails.DescribeAsync(c,null,id,date,
                    "اليوم أو الشهر مغلق؛ لا يسمح بالتحضير أو التعديل إلا من المسار الاستثنائي المخوّل.",ct)
                    .ConfigureAwait(false));
            return AttendanceConflictDetails.Reject(BridgeErrorCodes.HistoricDayLocked,blocked);
        }
        var status=await Status(c,null,statusId,ct).ConfigureAwait(false);
        if(status is null)
            return Fail(BridgeErrorCodes.CustomStatusInactive,
                "الحالة المحددة غير نشطة.","statusId");
        var people=new JsonArray();
        var conflicts=new JsonArray();
        string? firstError=null;
        foreach(var id in ids)
        {
            var s=await GetSnapshot(c,null,id,date,ct).ConfigureAwait(false);
            if(s is null)
            {
                conflicts.Add(await AttendanceConflictDetails.DescribeAsync(c,null,id,date,
                    "الفرد غير نشط أو منقول اليوم أو ليس له انتساب صالح إلى موقع؛ لا يمكن تحضيره.",ct)
                    .ConfigureAwait(false));
                firstError??=BridgeErrorCodes.TransferDayAttendanceForbidden;
                continue;
            }
            if(siteFilter.Length>0 && s.SiteId!=siteFilter)
            {
                conflicts.Add(AttendanceConflictDetails.Known(s.PersonId,s.Name,s.Number,
                    "موقع الفرد الحالي «"+s.SiteName+"» لا يطابق الموقع المحدد."));
                firstError??=BridgeErrorCodes.TransferDayAttendanceForbidden;
                continue;
            }
            if(mode=="initial" && s.PreviousRevision!=0)
            {
                conflicts.Add(AttendanceConflictDetails.Known(s.PersonId,s.Name,s.Number,
                    "الحالة «"+s.PreviousName+"» مسجلة بالفعل اليوم؛ استخدم تغيير الحالة."));
                firstError??=BridgeErrorCodes.AlreadyPrepared;
                continue;
            }
            if(mode=="revise" && s.PreviousRevision==0)
            {
                conflicts.Add(AttendanceConflictDetails.Known(s.PersonId,s.Name,s.Number,
                    "لا توجد حالة تمّام سابقة لهذا اليوم حتى تُعدّل."));
                firstError??=BridgeErrorCodes.ValidationError;
                continue;
            }
            if(mode=="revise" && s.PreviousStatus==statusId)
            {
                conflicts.Add(AttendanceConflictDetails.Known(s.PersonId,s.Name,s.Number,
                    "الحالة «"+status.Value.Name+"» هي نفسها الحالة المعتمدة؛ لم تُنشأ مراجعة مكررة."));
                firstError??=BridgeErrorCodes.ValidationError;
                continue;
            }
            people.Add(ToJson(s));
        }
        if(conflicts.Count>0)
            return AttendanceConflictDetails.Reject(firstError??BridgeErrorCodes.ConflictStale,conflicts);
        var sealedData=new JsonObject
        {
            ["businessDate"]=date,["statusId"]=statusId,["statusName"]=status.Value.Name,
            ["statusSymbol"]=status.Value.Symbol,["statusColor"]=status.Value.Color,
            ["mode"]=mode,["reason"]=reason,["persons"]=people,
            ["rangeStartDate"]=AttendanceDateRangePolicy.RequiresRange(statusId)?rangeStart:null,
            ["rangeEndDate"]=AttendanceDateRangePolicy.RequiresRange(statusId)?rangeEnd:null
        };
        string token=BridgePreviewTokens.Issue(_db,_time,"ATTENDANCE-"+mode,actor.UserId,
            sealedData,0,ct);
        var summary=(JsonObject)sealedData.DeepClone();
        summary["previewToken"]=token;
        summary["count"]=ids.Length;
        return BridgeHelpers.Ok(summary);
    }

    public async Task<BridgeResponse> CommitAsync(JsonObject? payload,BridgePrincipal actor,
        bool revise,CancellationToken ct)
    {
        string token=Read(payload,"previewToken"),mode=revise?"revise":"initial";
        if(token.Length<32)return Fail(BridgeErrorCodes.ValidationError,
            "معرّف المعاينة مطلوب.","previewToken");
        return await _db.ExecuteInWriteTransactionAsync(async(c,t)=>
        {
            // A lost WebView2 response may be retried with the same user-intent ID.
            // Replay lookup MUST precede one-use preview and daily cutoff checks:
            // an already committed action returns its original result, while a
            // different payload reusing the ID fails without touching evidence.
            var (replay,receipt)=await MutationIdempotency.StartAsync(
                c,t,revise?"ReviseTodayAttendance":"SubmitAttendance",actor.UserId,payload,ct)
                .ConfigureAwait(false);
            if(replay is not null)
            {
                if(replay.Success && replay.Data is JsonObject prior &&
                    prior["auditEventId"] is JsonValue id)
                    return replay with {AuditEventId=id.GetValue<string>()};
                return replay;
            }
            if(CutoffReached())return Fail(BridgeErrorCodes.HistoricDayLocked,
                "انتهت صلاحية معاينة التحضير بعد وقت القفل المحدد في الإعدادات.");
            using var find=c.CreateCommand();find.Transaction=t;
            find.CommandText="""
                SELECT PayloadCanonicalJson FROM PreviewTokens
                WHERE Token=$token AND Operation=$operation AND ActorUserId=$actor
                  AND ConsumedFlag=0 AND ExpiresAtUtc>$now;
                """;
            find.Parameters.AddWithValue("$token",token);
            find.Parameters.AddWithValue("$operation","ATTENDANCE-"+mode);
            find.Parameters.AddWithValue("$actor",actor.UserId);
            find.Parameters.AddWithValue("$now",_time.GetUtcNow().ToString("O"));
            string? data=await find.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            if(data is null)return Fail(BridgeErrorCodes.ConflictStale,
                "انتهت المعاينة أو استُخدمت مسبقاً. أعد عرض الكشف والتأكيد.")
                with {ConflictInfo=new JsonObject{
                    ["reason"]="InvalidOrExpiredPreviewToken"
                }};
            var info=JsonNode.Parse(data) as JsonObject;
            string date=Read(info,"businessDate"),statusId=Read(info,"statusId");
            if(info?["persons"] is not JsonArray selected || selected.Count==0 ||
               selected.Count>100 || date!=Today() || Read(info,"mode")!=mode)
                return Fail(BridgeErrorCodes.HistoricDayLocked,"المعاينة ليست صالحة لليوم الحالي.");
            if(await Closed(c,t,date,ct).ConfigureAwait(false))
                return Fail(BridgeErrorCodes.HistoricDayLocked,"تم إغلاق اليوم بعد إنشاء المعاينة.");
            if(!AttendanceDateRangePolicy.TryParse(info,statusId,Today(),out var rangeStart,out var rangeEnd,out var rangeError))
                return Fail(BridgeErrorCodes.ValidationError,rangeError,"rangeStartDate");
            var status=await Status(c,t,statusId,ct).ConfigureAwait(false);
            if(status is null)return Fail(BridgeErrorCodes.CustomStatusInactive,
                "الحالة توقفت عن العمل بعد المعاينة.");
            var snapshots=new List<Snapshot>();
            var conflicts=new JsonArray();
            foreach(var item in selected)
            {
                if(item is not JsonObject prev)
                    return Fail(BridgeErrorCodes.ValidationError,"المعاينة غير صالحة.");
                string personId=Read(prev,"personId");
                var current=await GetSnapshot(c,t,personId,date,ct).ConfigureAwait(false);
                long revision;
                try{revision=prev["previousRevision"]!.GetValue<long>();}
                catch{return Fail(BridgeErrorCodes.ValidationError,"مراجعة المعاينة غير صالحة.");}
                if(current is null)
                {
                    conflicts.Add(await AttendanceConflictDetails.DescribeAsync(c,t,personId,date,
                        "الفرد غير متاح للتحضير الآن؛ تغير وضعه أو حركته منذ المعاينة.",ct)
                        .ConfigureAwait(false));
                    continue;
                }
                if(current.AssignmentId!=Read(prev,"assignmentId")
                    || current.PreviousRevision!=revision
                    || (current.PreviousStatus??"")!=Read(prev,"previousStatusId"))
                {
                    conflicts.Add(AttendanceConflictDetails.Known(current.PersonId,
                        current.Name,current.Number,
                        "تغيرت الحالة أو الموقع بعد المعاينة؛ أعد تحميل الكشف."));
                    continue;
                }
                if(!revise && revision!=0)
                {
                    conflicts.Add(AttendanceConflictDetails.Known(current.PersonId,
                        current.Name,current.Number,"تم تحضير الفرد مسبقاً بعد المعاينة."));
                    continue;
                }
                if(revise && (revision==0||current.PreviousStatus==statusId))
                {
                    conflicts.Add(AttendanceConflictDetails.Known(current.PersonId,
                        current.Name,current.Number,
                        "الحالة الحالية لا تقبل التعديل المطلوب أو لا يوجد تغير فعلي."));
                    continue;
                }
                snapshots.Add(current);
            }
            if(conflicts.Count>0)
                return AttendanceConflictDetails.Reject(BridgeErrorCodes.ConflictStale,conflicts);
            // Refuse overlapping future schedules before consuming the preview.
            foreach(var person in snapshots)
            {
                using var overlap=c.CreateCommand();overlap.Transaction=t;
                overlap.CommandText="""
                    SELECT EXISTS(SELECT 1 FROM AttendanceDateRanges
                      WHERE PersonId=$person AND EndedAtUtc IS NULL
                        AND StartLocalDate <= $end AND EndLocalDate >= $start);
                    """;
                overlap.Parameters.AddWithValue("$person",person.PersonId);
                overlap.Parameters.AddWithValue("$start",date);
                overlap.Parameters.AddWithValue("$end",
                    AttendanceDateRangePolicy.RequiresRange(statusId)?rangeEnd:date);
                // A same-day revision can replace an existing range, but never
                // silently overwrite another future schedule.
                if(Convert.ToInt32(await overlap.ExecuteScalarAsync(ct).ConfigureAwait(false))!=0)
                {
                    using var existing=c.CreateCommand();existing.Transaction=t;
                    existing.CommandText="""
                        SELECT StartLocalDate,EndLocalDate FROM AttendanceDateRanges
                        WHERE PersonId=$person AND EndedAtUtc IS NULL
                          AND StartLocalDate <= $end AND EndLocalDate >= $start LIMIT 1;
                        """;
                    existing.Parameters.AddWithValue("$person",person.PersonId);
                    existing.Parameters.AddWithValue("$start",date);
                    existing.Parameters.AddWithValue("$end",
                        AttendanceDateRangePolicy.RequiresRange(statusId)?rangeEnd:date);
                    using var r=await existing.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if(await r.ReadAsync(ct).ConfigureAwait(false) &&
                       (!revise || (AttendanceDateRangePolicy.RequiresRange(statusId) && string.CompareOrdinal(r.GetString(0),date)>0)))
                        return Fail(BridgeErrorCodes.ValidationError,
                            "توجد مدة إجازة أو إذن متداخلة لهذا الفرد؛ راجع المدة السابقة.");
                }
            }
            long? consumed=await BridgePreviewTokens.ConsumeAsync(_db,_time,c,t,token,
                "ATTENDANCE-"+mode,actor.UserId,0,ct).ConfigureAwait(false);
            if(consumed!=0)return Fail(BridgeErrorCodes.ConflictStale,
                "معاينة قديمة أو مستخدمة.")
                with {ConflictInfo=new JsonObject{
                    ["reason"]="InvalidOrExpiredPreviewToken"
                }};
            string now=_time.GetUtcNow().ToString("O"),reason=Read(info,"reason");
            string corr=Guid.NewGuid().ToString("N");
            var auditEventIds=new JsonArray();
            foreach(var person in snapshots)
            {
                using var cmd=c.CreateCommand();cmd.Transaction=t;
                if(!revise)
                {
                    cmd.CommandText="""
                        INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                            StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                            AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                            Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
                        VALUES($person,$date,$status,$name,$symbol,$color,$assign,$site,$siteName,
                            $source,$actor,$now,1,$key);
                        """;
                    cmd.Parameters.AddWithValue("$assign",person.AssignmentId);
                    cmd.Parameters.AddWithValue("$site",person.SiteId);
                    cmd.Parameters.AddWithValue("$siteName",person.SiteName);
                    cmd.Parameters.AddWithValue("$source",snapshots.Count==1?"manual":"bulk");
                    cmd.Parameters.AddWithValue("$key",token+":"+person.PersonId);
                }
                else
                {
                    cmd.CommandText="""
                        UPDATE AttendanceRecords SET StatusId=$status,StatusNameSnapshot=$name,
                            StatusSymbolSnapshot=$symbol,StatusColorSnapshot=$color,
                            Source='manual',CreatedByUserId=$actor,CommittedAtUtc=$now,
                            RevisionNumber=RevisionNumber+1
                        WHERE PersonId=$person AND AttendanceLocalDate=$date
                          AND RevisionNumber=$revision;
                        """;
                    cmd.Parameters.AddWithValue("$revision",person.PreviousRevision);
                }
                cmd.Parameters.AddWithValue("$person",person.PersonId);
                cmd.Parameters.AddWithValue("$date",date);
                cmd.Parameters.AddWithValue("$status",statusId);
                cmd.Parameters.AddWithValue("$name",status.Value.Name);
                cmd.Parameters.AddWithValue("$symbol",status.Value.Symbol);
                cmd.Parameters.AddWithValue("$color",status.Value.Color);
                cmd.Parameters.AddWithValue("$actor",actor.UserId);
                cmd.Parameters.AddWithValue("$now",now);
                int affected=await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if(affected!=1)throw new InvalidOperationException("Attendance concurrency conflict.");
                // A manual revision ends any period covering the current day, even one
                // that started earlier. Historical attendance records remain intact.
                if(revise)
                {
                    using var expire=c.CreateCommand();expire.Transaction=t;
                    expire.CommandText="""
                        UPDATE AttendanceDateRanges SET EndedAtUtc=$now
                        WHERE PersonId=$person AND EndedAtUtc IS NULL
                          AND StartLocalDate<=$date AND EndLocalDate>=$date;
                        """;
                    expire.Parameters.AddWithValue("$now",now);
                    expire.Parameters.AddWithValue("$person",person.PersonId);
                    expire.Parameters.AddWithValue("$date",date);
                    await expire.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                if(AttendanceDateRangePolicy.RequiresRange(statusId))
                {
                    using var range=c.CreateCommand();range.Transaction=t;
                    range.CommandText="""
                        INSERT INTO AttendanceDateRanges
                        (RangeId,PersonId,StatusId,StartLocalDate,EndLocalDate,CreatedAtUtc,CreatedByUserId)
                        VALUES($id,$person,$status,$start,$end,$now,$actor);
                        """;
                    range.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("N"));
                    range.Parameters.AddWithValue("$person",person.PersonId);
                    range.Parameters.AddWithValue("$status",statusId);
                    range.Parameters.AddWithValue("$start",rangeStart);
                    range.Parameters.AddWithValue("$end",rangeEnd);
                    range.Parameters.AddWithValue("$now",now);
                    range.Parameters.AddWithValue("$actor",actor.UserId);
                    await range.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                if(revise)
                {
                    using var revision=c.CreateCommand();revision.Transaction=t;
                    revision.CommandText="""
                        INSERT INTO AttendanceRevisions(PersonId,TargetLocalDate,BeforeStatusId,
                            BeforeStatusNameSnapshot,AfterStatusId,AfterStatusNameSnapshot,
                            Reason,ActorUserId,ExecutedAtUtc,AuthorizationMethod)
                        VALUES($person,$date,$before,$beforeName,$after,$afterName,
                            $reason,$actor,$now,'authenticated-session-preview');
                        """;
                    revision.Parameters.AddWithValue("$person",person.PersonId);
                    revision.Parameters.AddWithValue("$date",date);
                    revision.Parameters.AddWithValue("$before",person.PreviousStatus!);
                    revision.Parameters.AddWithValue("$beforeName",person.PreviousName);
                    revision.Parameters.AddWithValue("$after",statusId);
                    revision.Parameters.AddWithValue("$afterName",status.Value.Name);
                    revision.Parameters.AddWithValue("$reason",reason);
                    revision.Parameters.AddWithValue("$actor",actor.UserId);
                    revision.Parameters.AddWithValue("$now",now);
                    await revision.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                string auditEventId=await BridgeAuditWriter.WriteAsync(c,t,_time,
                    revise?"ATTENDANCE_REVISE":"ATTENDANCE_COMMIT",
                    revise?"تعديل التحضير":"اعتماد التحضير",
                    actor.UserId,actor.Username,actor.Username,"Person",person.PersonId,person.Name,
                    new JsonObject{["statusId"]=person.PreviousStatus,["statusName"]=person.PreviousName}.ToJsonString(),
                    new JsonObject{["statusId"]=statusId,["statusName"]=status.Value.Name,
                        ["date"]=date,["serviceNumberA"]=person.Number,["site"]=person.SiteName,
                        ["rangeStartDate"]=AttendanceDateRangePolicy.RequiresRange(statusId)?rangeStart:null,
                        ["rangeEndDate"]=AttendanceDateRangePolicy.RequiresRange(statusId)?rangeEnd:null}.ToJsonString(),
                    revise?"تعديل تحضير اليوم بسبب: "+reason:"تحضير اليوم",
                    revise?reason:null,date,corr,ct).ConfigureAwait(false);
                auditEventIds.Add(auditEventId);
            }
            string primaryAuditEventId=auditEventIds[0]!.GetValue<string>();
            var result=new JsonObject
            {
                ["count"]=snapshots.Count,["date"]=date,["statusId"]=statusId,
                ["revised"]=revise,
                // For bulk commits, AuditEventId identifies one real event;
                // auditEventIds carries the complete one-to-one person/event set.
                ["auditEventId"]=primaryAuditEventId,
                ["auditEventIds"]=auditEventIds
            };
            await MutationIdempotency.StoreAsync(c,t,receipt,result,_time,ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(result,events:BridgeHelpers.InvalidationEvent(
                revise?UiInvalidationEvents.AttendanceRevised:UiInvalidationEvents.AttendanceCommitted,
                date,snapshots.Select(p=>p.PersonId)))
                with {AuditEventId=primaryAuditEventId};
        },ct).ConfigureAwait(false);
    }
}
