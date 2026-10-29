using System.Globalization;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Immutable-key lifetime reporting across encrypted attendance, identity,
/// movement and append-only audit. Paged source events; no write or PII export.
/// </summary>
public sealed class PersonnelReportService
{
    private readonly AlTamamDbContext _db;
    public PersonnelReportService(AlTamamDbContext db)=>_db=db;

    private static string Text(JsonObject? node,string key)
    { try{return node?[key]?.GetValue<string>()?.Trim()??"";}catch{return "";} }
    private static int Page(JsonObject? node,string key)
    { try{return Math.Clamp(node?[key]?.GetValue<int>()??1,1,100000);}catch{return 1;} }
    private static BridgeResponse NotFound()=>BridgeHelpers.Error(
        BridgeErrorCodes.MissingData,"لم يُعثر على الفرد في سجلات البرنامج.");

    private sealed record ReportFilters(string From,string To,string SiteId,
        string StatusId,int MovementType)
    {
        public void Bind(SqliteCommand command)
        {
            // Microsoft.Data.Sqlite rejects unused named parameters. Only bind
            // the predicates present in this particular projection.
            void Add(string name,object value)
            {
                if(command.CommandText.Contains(name,StringComparison.Ordinal))
                    command.Parameters.AddWithValue(name,value);
            }
            Add("$from",From);
            Add("$to",To);
            Add("$site",SiteId);
            Add("$status",StatusId);
            Add("$moveType",MovementType);
        }
    }

    private static bool TryReadFilters(JsonObject? payload,out ReportFilters filters,
        out BridgeResponse? problem)
    {
        string from=Text(payload,"dateFrom"),to=Text(payload,"dateTo"),
            site=Text(payload,"siteId"),status=Text(payload,"statusId");
        int movementType=0;
        try {movementType=payload?["movementType"]?.GetValue<int>()??0;}
        catch {movementType=-1;}
        static bool ValidDate(string text)=>DateOnly.TryParseExact(text,
            "yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _);
        filters=new ReportFilters(from,to,site,status,movementType);
        problem=null;
        if ((from.Length>0&&!ValidDate(from))||(to.Length>0&&!ValidDate(to))||
            (from.Length>0&&to.Length>0&&string.CompareOrdinal(from,to)>0)||
            site.Length>128||status.Length>128||
            site.Any(char.IsControl)||status.Any(char.IsControl)||
            movementType is <0 or >4)
        {
            problem=BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "مرشحات التقرير غير صالحة؛ تحقق من الفترة والموقع والحالة ونوع الحركة.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Combined read-only historical report. Each row is isolated by immutable
    /// PersonId even when two people have reused the same displayed الرقم (ع).
    /// The chosen date boundaries are inclusive; only committed facts are counted.
    /// </summary>
    private async Task<BridgeResponse> GroupReportAsync(JsonObject payload,CancellationToken ct)
    {
        if(payload["personIds"] is not JsonArray selected || selected.Count is <1 or >100)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "اختر من فرد إلى 100 فرد لإعداد التقرير الجماعي.");
        var ids=new List<string>(selected.Count);
        var unique=new HashSet<string>(StringComparer.Ordinal);
        foreach(var node in selected)
        {
            string id;
            try{id=node?.GetValue<string>()??"";}
            catch{return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,"معرف فرد غير صالح.");}
            if(id.Length is <3 or >128 || !unique.Add(id))
                return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                    "حدد أفراداً بمعرفات داخلية صحيحة وغير مكررة.");
            ids.Add(id);
        }

        if(!TryReadFilters(payload,out var filters,out var problem))
            return problem!;
        string from=filters.From,to=filters.To;

        string placeholders=string.Join(",",ids.Select((_,i)=>"$person"+i));
        using var connection=_db.CreateOpenConnection();
        void BindPeople(SqliteCommand command)
        {
            for(int i=0;i<ids.Count;i++)
                command.Parameters.AddWithValue("$person"+i,ids[i]);
        }
        void BindRange(SqliteCommand command)
        {
            BindPeople(command);
            filters.Bind(command);
        }
        var people=new Dictionary<string,JsonObject>(StringComparer.Ordinal);
        using(var query=connection.CreateCommand())
        {
            query.CommandText=$"""
                SELECT PersonId,CurrentVisibleName,CurrentServiceNumberA,
                       FirstRecordedLocalDate,OrganizationalStatus,ActiveFlag
                FROM Persons WHERE PersonId IN ({placeholders});
                """;
            BindPeople(query);
            using var reader=await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while(await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string id=reader.GetString(0);
                people.Add(id,new JsonObject
                {
                    ["personId"]=id,["name"]=reader.GetString(1),
                    ["serviceNumberA"]=reader.GetString(2),
                    ["firstRecordedDate"]=reader.GetString(3),
                    ["organizationalStatus"]=reader.GetInt32(4),
                    ["active"]=reader.GetInt32(5)==1,
                    ["attendanceTotal"]=0L,["movementTotal"]=0L,
                    ["statusTotals"]=new JsonArray()
                });
            }
        }
        // All-or-nothing: never silently omit an unknown or stale selected ID.
        if(people.Count!=ids.Count)return NotFound();

        var totals=new Dictionary<(string StatusId,string Snapshot),long>();
        long allAttendance=0,allMovements=0;
        using(var query=connection.CreateCommand())
        {
            query.CommandText=$"""
                SELECT PersonId,StatusId,StatusNameSnapshot,COUNT(*)
                FROM AttendanceRecords
                WHERE PersonId IN ({placeholders})
                  AND ($from='' OR AttendanceLocalDate >= $from)
                  AND ($to='' OR AttendanceLocalDate <= $to)
                  AND ($site='' OR SiteIdAtEffectiveDate=$site)
                  AND ($status='' OR StatusId=$status)
                GROUP BY PersonId,StatusId,StatusNameSnapshot
                ORDER BY PersonId,StatusNameSnapshot,StatusId;
                """;
            BindRange(query);
            using var reader=await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while(await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string id=reader.GetString(0),statusId=reader.GetString(1),
                    snapshot=reader.GetString(2);
                long count=reader.GetInt64(3);
                var row=people[id];
                row["attendanceTotal"]=row["attendanceTotal"]!.GetValue<long>()+count;
                ((JsonArray)row["statusTotals"]!).Add(new JsonObject{
                    ["statusId"]=statusId,["statusName"]=snapshot,["count"]=count
                });
                totals[(statusId,snapshot)]=totals.GetValueOrDefault((statusId,snapshot))+count;
                allAttendance+=count;
            }
        }
        using(var query=connection.CreateCommand())
        {
            query.CommandText=$"""
                SELECT PersonId,COUNT(*) FROM PersonnelMovements
                WHERE PersonId IN ({placeholders})
                  AND ($from='' OR EffectiveLocalDate >= $from)
                  AND ($to='' OR EffectiveLocalDate <= $to)
                  AND ($site='' OR SourceSiteId=$site OR DestinationSiteId=$site)
                  AND ($moveType=0 OR MovementType=$moveType)
                GROUP BY PersonId;
                """;
            BindRange(query);
            using var reader=await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while(await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var count=reader.GetInt64(1);
                people[reader.GetString(0)]["movementTotal"]=count;
                allMovements+=count;
            }
        }
        var statusTotals=new JsonArray();
        foreach(var entry in totals.OrderBy(x=>x.Key.Snapshot,StringComparer.Ordinal)
                                   .ThenBy(x=>x.Key.StatusId,StringComparer.Ordinal))
            statusTotals.Add(new JsonObject{
                ["statusId"]=entry.Key.StatusId,
                ["statusName"]=entry.Key.Snapshot,["count"]=entry.Value
            });
        return BridgeHelpers.Ok(new JsonObject
        {
            ["mode"]="group",["personCount"]=ids.Count,
            ["dateFrom"]=from,["dateTo"]=to,
            ["siteId"]=filters.SiteId,["statusId"]=filters.StatusId,
            ["movementType"]=filters.MovementType,
            ["persons"]=new JsonArray(ids.Select(id=>(JsonNode?)people[id]).ToArray()),
            ["attendanceTotal"]=allAttendance,
            ["movementTotal"]=allMovements,
            ["statusTotals"]=statusTotals
        });
    }

    public async Task<BridgeResponse> ReportAsync(JsonObject? payload,CancellationToken ct)
    {
        // Keep the ONE canonical reporting action and screen. A group request is
        // explicit, bounded, read-only, and keyed EXCLUSIVELY by immutable PersonId.
        if(payload?.ContainsKey("personIds")==true)
            return await GroupReportAsync(payload,ct).ConfigureAwait(false);
        if(!TryReadFilters(payload,out var filters,out var invalid))
            return invalid!;
        string personId=Text(payload,"personId");
        if(personId.Length<3||personId.Length>128)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,"حدد الفرد من كشف البرنامج.");
        int attendPage=Page(payload,"attendancePage"),auditPage=Page(payload,"auditPage"),
            revisionPage=Page(payload,"revisionPage"),
            identityPage=Page(payload,"identityPage"),movementPage=Page(payload,"movementPage"),
            sitePage=Page(payload,"sitePage");
        const int limit=100;
        using var conn=_db.CreateOpenConnection();
        using var person=conn.CreateCommand();
        person.CommandText="""
            SELECT PersonId,CurrentVisibleName,CurrentServiceNumberA,
                   FirstRecordedLocalDate,OrganizationalStatus,ActiveFlag
            FROM Persons WHERE PersonId=$id;
            """;
        person.Parameters.AddWithValue("$id",personId);
        JsonObject identity;
        using(var r=await person.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if(!await r.ReadAsync(ct).ConfigureAwait(false))return NotFound();
            identity=new JsonObject
            {
                ["personId"]=r.GetString(0),["name"]=r.GetString(1),
                ["serviceNumberA"]=r.GetString(2),
                ["firstRecordedDate"]=r.GetString(3),
                ["organizationalStatus"]=r.GetInt32(4),
                ["active"]=r.GetInt32(5)==1
            };
        }
        // Today's display identity never retroactively changes saved events.
        using var status=conn.CreateCommand();
        status.CommandText="""
            SELECT StatusId,StatusNameSnapshot,COUNT(*),MIN(AttendanceLocalDate),MAX(AttendanceLocalDate)
            FROM AttendanceRecords WHERE PersonId=$id
              AND ($from='' OR AttendanceLocalDate >= $from)
              AND ($to='' OR AttendanceLocalDate <= $to)
              AND ($site='' OR SiteIdAtEffectiveDate=$site)
              AND ($status='' OR StatusId=$status)
            GROUP BY StatusId,StatusNameSnapshot
            ORDER BY SUM(1) DESC,StatusNameSnapshot;
            """;
        status.Parameters.AddWithValue("$id",personId);
        filters.Bind(status);
        var totals=new JsonArray();
        using(var r=await status.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while(await r.ReadAsync(ct).ConfigureAwait(false))
                totals.Add(new JsonObject
                {
                    ["statusId"]=r.GetString(0),["statusName"]=r.GetString(1),
                    ["count"]=r.GetInt64(2),["firstDate"]=r.GetString(3),
                    ["lastDate"]=r.GetString(4)
                });

        using var months=conn.CreateCommand();
        months.CommandText="""
            SELECT SUBSTR(AttendanceLocalDate,1,7),StatusId,
                   StatusNameSnapshot,COUNT(*)
            FROM AttendanceRecords WHERE PersonId=$id
              AND ($from='' OR AttendanceLocalDate >= $from)
              AND ($to='' OR AttendanceLocalDate <= $to)
              AND ($site='' OR SiteIdAtEffectiveDate=$site)
              AND ($status='' OR StatusId=$status)
            GROUP BY SUBSTR(AttendanceLocalDate,1,7),StatusId,StatusNameSnapshot
            ORDER BY 1 DESC,3;
            """;
        months.Parameters.AddWithValue("$id",personId);
        filters.Bind(months);
        var monthly=new JsonArray();
        using(var r=await months.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while(await r.ReadAsync(ct).ConfigureAwait(false))
                monthly.Add(new JsonObject
                {
                    ["period"]=r.GetString(0),["statusId"]=r.GetString(1),
                    ["statusName"]=r.GetString(2),["count"]=r.GetInt64(3)
                });

        using var attendanceCount=conn.CreateCommand();
        attendanceCount.CommandText="""
            SELECT COUNT(*) FROM AttendanceRecords WHERE PersonId=$id
              AND ($from='' OR AttendanceLocalDate >= $from)
              AND ($to='' OR AttendanceLocalDate <= $to)
              AND ($site='' OR SiteIdAtEffectiveDate=$site)
              AND ($status='' OR StatusId=$status);
            """;
        attendanceCount.Parameters.AddWithValue("$id",personId);
        filters.Bind(attendanceCount);
        long totalDays=Convert.ToInt64(await attendanceCount.ExecuteScalarAsync(ct).ConfigureAwait(false));
        using var attendance=conn.CreateCommand();
        attendance.CommandText="""
            SELECT ar.AttendanceLocalDate,ar.StatusNameSnapshot,ar.StatusSymbolSnapshot,
                   ar.SiteNameSnapshot,ar.Source,ar.RevisionNumber,ar.CommittedAtUtc,
                   (SELECT h.NameSnapshot FROM PersonIdentityHistory h
                    WHERE h.PersonId=ar.PersonId AND h.ValidFromLocalDate<=ar.AttendanceLocalDate
                      AND (h.ValidToLocalDate IS NULL OR h.ValidToLocalDate>ar.AttendanceLocalDate)
                    ORDER BY h.ValidFromLocalDate DESC LIMIT 1),
                   (SELECT h.ServiceNumberASnapshot FROM PersonIdentityHistory h
                    WHERE h.PersonId=ar.PersonId AND h.ValidFromLocalDate<=ar.AttendanceLocalDate
                      AND (h.ValidToLocalDate IS NULL OR h.ValidToLocalDate>ar.AttendanceLocalDate)
                    ORDER BY h.ValidFromLocalDate DESC LIMIT 1)
            FROM AttendanceRecords ar WHERE ar.PersonId=$id
              AND ($from='' OR ar.AttendanceLocalDate >= $from)
              AND ($to='' OR ar.AttendanceLocalDate <= $to)
              AND ($site='' OR ar.SiteIdAtEffectiveDate=$site)
              AND ($status='' OR ar.StatusId=$status)
            ORDER BY AttendanceLocalDate DESC LIMIT $size OFFSET $offset;
            """;
        attendance.Parameters.AddWithValue("$id",personId);
        filters.Bind(attendance);
        attendance.Parameters.AddWithValue("$size",limit);
        attendance.Parameters.AddWithValue("$offset",(long)(attendPage-1)*limit);
        var daily=new JsonArray();
        using(var r=await attendance.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while(await r.ReadAsync(ct).ConfigureAwait(false))
                daily.Add(new JsonObject
                {
                    ["date"]=r.GetString(0),["statusName"]=r.GetString(1),
                    ["symbol"]=r.GetString(2),["site"]=r.GetString(3),
                    ["source"]=r.GetString(4),["revision"]=r.GetInt64(5),
                    ["committedAtUtc"]=r.GetString(6),
                    // Null explicitly means the historic identity was not recorded.
                    // Never substitute today's display name/number for missing evidence.
                    ["name"]=r.IsDBNull(7)?null:r.GetString(7),
                    ["serviceNumberA"]=r.IsDBNull(8)?null:r.GetString(8)
                });

        using var identityCount=conn.CreateCommand();
        identityCount.CommandText="SELECT COUNT(*) FROM PersonIdentityHistory WHERE PersonId=$id;";
        identityCount.Parameters.AddWithValue("$id",personId);
        long totalIdentities=Convert.ToInt64(await identityCount.ExecuteScalarAsync(ct).ConfigureAwait(false));
        using var histories=conn.CreateCommand();
        histories.CommandText="""
            SELECT NameSnapshot,ServiceNumberASnapshot,ValidFromLocalDate,
                   ValidToLocalDate,ChangedByUserId,Reason
            FROM PersonIdentityHistory WHERE PersonId=$id
            ORDER BY ValidFromLocalDate DESC,HistoryId DESC LIMIT $size OFFSET $offset;
            """;
        histories.Parameters.AddWithValue("$id",personId);
        histories.Parameters.AddWithValue("$size",limit);
        histories.Parameters.AddWithValue("$offset",(long)(identityPage-1)*limit);
        var names=new JsonArray();
        using(var r=await histories.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while(await r.ReadAsync(ct).ConfigureAwait(false))
                names.Add(new JsonObject
                {
                    ["name"]=r.GetString(0),["serviceNumberA"]=r.GetString(1),
                    ["from"]=r.GetString(2),["to"]=r.IsDBNull(3)?null:r.GetString(3),
                    ["actorUserId"]=r.GetString(4),["reason"]=r.GetString(5)
                });

        using var movementCount=conn.CreateCommand();
        movementCount.CommandText="""
            SELECT COUNT(*) FROM PersonnelMovements WHERE PersonId=$id
              AND ($from='' OR EffectiveLocalDate >= $from)
              AND ($to='' OR EffectiveLocalDate <= $to)
              AND ($site='' OR SourceSiteId=$site OR DestinationSiteId=$site)
              AND ($moveType=0 OR MovementType=$moveType);
            """;
        movementCount.Parameters.AddWithValue("$id",personId);
        filters.Bind(movementCount);
        long totalMovements=Convert.ToInt64(await movementCount.ExecuteScalarAsync(ct).ConfigureAwait(false));
        using var movements=conn.CreateCommand();
        movements.CommandText="""
            SELECT m.MovementType,m.SourceSiteNameSnapshot,
                   COALESCE(m.DestinationSiteNameSnapshot,m.DestinationExternalName,''),
                   m.SeparationReason,m.EffectiveLocalDate,m.CommittedAtUtc,m.ActorUserId,
                   m.PersonNameSnapshot,m.ServiceNumberASnapshot,
                   COALESCE((SELECT a.ActorUsernameSnapshot || ' - ' ||
                             COALESCE(a.ActorRosterFullNameSnapshot,'')
                     FROM AuditEvents a
                     WHERE a.TargetEntityType='Person' AND a.TargetEntityId=m.PersonId
                       AND a.ActorUserId=m.ActorUserId
                       AND a.EffectiveLocalDate=m.EffectiveLocalDate
                       AND a.OperationCode=CASE m.MovementType
                           WHEN 1 THEN 'TRANSFER_INTERNAL'
                           WHEN 2 THEN 'TRANSFER_EXTERNAL'
                           WHEN 3 THEN 'PERSON_SEPARATE'
                           ELSE 'PERSON_RETURN' END
                     ORDER BY a.ExecutedAtUtc DESC LIMIT 1),'—')
            FROM PersonnelMovements m WHERE m.PersonId=$id
              AND ($from='' OR m.EffectiveLocalDate >= $from)
              AND ($to='' OR m.EffectiveLocalDate <= $to)
              AND ($site='' OR m.SourceSiteId=$site OR m.DestinationSiteId=$site)
              AND ($moveType=0 OR m.MovementType=$moveType)
            ORDER BY m.EffectiveLocalDate DESC,m.CommittedAtUtc DESC LIMIT $size OFFSET $offset;
            """;
        movements.Parameters.AddWithValue("$id",personId);
        filters.Bind(movements);
        movements.Parameters.AddWithValue("$size",limit);
        movements.Parameters.AddWithValue("$offset",(long)(movementPage-1)*limit);
        var moves=new JsonArray();
        using(var r=await movements.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while(await r.ReadAsync(ct).ConfigureAwait(false))
                moves.Add(new JsonObject
                {
                    ["type"]=r.GetInt32(0)==1?"نقل داخلي":
                        r.GetInt32(0)==2?"نقل خارجي":
                    r.GetInt32(0)==3?"فصل":"عودة للخدمة",
                    ["source"]=r.GetString(1),["destination"]=r.GetString(2),
                    ["reason"]=r.IsDBNull(3)?null:r.GetString(3),
                    ["date"]=r.GetString(4),["committedAtUtc"]=r.GetString(5),
                    ["actorUserId"]=r.GetString(6),
                    ["name"]=r.GetString(7),["serviceNumberA"]=r.GetString(8),
                    ["executor"]=r.GetString(9)
                });

        using var auditCount=conn.CreateCommand();
        auditCount.CommandText="""
            SELECT COUNT(*) FROM AuditEvents
            WHERE TargetEntityType='Person' AND TargetEntityId=$id
              AND ($from='' OR COALESCE(NULLIF(EffectiveLocalDate,''),
                  SUBSTR(ExecutedAtUtc,1,10)) >= $from)
              AND ($to='' OR COALESCE(NULLIF(EffectiveLocalDate,''),
                  SUBSTR(ExecutedAtUtc,1,10)) <= $to);
            """;
        auditCount.Parameters.AddWithValue("$id",personId);
        filters.Bind(auditCount);
        long audits=Convert.ToInt64(await auditCount.ExecuteScalarAsync(ct).ConfigureAwait(false));
        using var audit=conn.CreateCommand();
        audit.CommandText="""
            SELECT OperationArabicName,ActorUsernameSnapshot,
                   COALESCE(ActorRosterFullNameSnapshot,''),
                   EffectiveLocalDate,ExecutedAtUtc,DefaultArabicDescription,Reason,
                   OldValueJson,NewValueJson
            FROM AuditEvents
            WHERE TargetEntityType='Person' AND TargetEntityId=$id
              AND ($from='' OR COALESCE(NULLIF(EffectiveLocalDate,''),
                  SUBSTR(ExecutedAtUtc,1,10)) >= $from)
              AND ($to='' OR COALESCE(NULLIF(EffectiveLocalDate,''),
                  SUBSTR(ExecutedAtUtc,1,10)) <= $to)
            ORDER BY ExecutedAtUtc DESC,AuditEventId DESC LIMIT $size OFFSET $offset;
            """;
        audit.Parameters.AddWithValue("$id",personId);
        filters.Bind(audit);
        audit.Parameters.AddWithValue("$size",limit);
        audit.Parameters.AddWithValue("$offset",(long)(auditPage-1)*limit);
        var events=new JsonArray();
        using(var r=await audit.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while(await r.ReadAsync(ct).ConfigureAwait(false))
                events.Add(new JsonObject
                {
                    ["operation"]=r.GetString(0),["username"]=r.GetString(1),
                    ["fullName"]=r.GetString(2),
                    ["executor"]=r.GetString(1)+" - "+r.GetString(2),
                    ["effectiveDate"]=r.IsDBNull(3)?null:r.GetString(3),
                    ["executedAtUtc"]=r.GetString(4),["description"]=r.GetString(5),
                    ["reason"]=r.IsDBNull(6)?null:r.GetString(6),
                    ["oldValueJson"]=r.IsDBNull(7)?null:r.GetString(7),
                    ["newValueJson"]=r.IsDBNull(8)?null:r.GetString(8)
                });
        // AttendanceRevisions is immutable evidence separate from current attendance
        // and audit presentation. Read it directly so every correction remains
        // visible, including revisions made to a subsequently dismissed person.
        const string revisionWhere="""
            FROM AttendanceRevisions rev WHERE rev.PersonId=$id
              AND ($from='' OR rev.TargetLocalDate >= $from)
              AND ($to='' OR rev.TargetLocalDate <= $to)
              AND ($status='' OR rev.BeforeStatusId=$status OR rev.AfterStatusId=$status)
              AND ($site='' OR EXISTS(
                  SELECT 1 FROM PersonAssignments assignment
                  WHERE assignment.PersonId=rev.PersonId AND assignment.SiteId=$site
                    AND assignment.ValidFromLocalDate<=rev.TargetLocalDate
                    AND (assignment.ValidToLocalDate IS NULL OR assignment.ValidToLocalDate>rev.TargetLocalDate)));
            """;
        using var revisionCount=conn.CreateCommand();
        revisionCount.CommandText="SELECT COUNT(*) "+revisionWhere;
        revisionCount.Parameters.AddWithValue("$id",personId);
        filters.Bind(revisionCount);
        long totalRevisions=Convert.ToInt64(await revisionCount.ExecuteScalarAsync(ct).ConfigureAwait(false));
        using var revisionsQuery=conn.CreateCommand();
        revisionsQuery.CommandText="""
            SELECT rev.TargetLocalDate,rev.BeforeStatusNameSnapshot,
                   rev.AfterStatusNameSnapshot,rev.Reason,rev.ExecutedAtUtc,
                   rev.AuthorizationMethod,
                   COALESCE((
                       SELECT audit.ActorUsernameSnapshot || ' - ' ||
                              COALESCE(audit.ActorRosterFullNameSnapshot,'')
                       FROM AuditEvents audit
                       WHERE audit.TargetEntityType='Person'
                         AND audit.TargetEntityId=rev.PersonId
                         AND audit.ActorUserId=rev.ActorUserId
                         AND audit.EffectiveLocalDate=rev.TargetLocalDate
                         AND audit.OperationCode IN ('ATTENDANCE_REVISE','CLOSED_DAY_CORRECTED')
                       ORDER BY audit.ExecutedAtUtc DESC
                       LIMIT 1),'غير موثق')
            """+revisionWhere.Replace(";","")+"""
            ORDER BY rev.TargetLocalDate DESC,rev.ExecutedAtUtc DESC
            LIMIT $size OFFSET $offset;
            """;
        revisionsQuery.Parameters.AddWithValue("$id",personId);
        filters.Bind(revisionsQuery);
        revisionsQuery.Parameters.AddWithValue("$size",limit);
        revisionsQuery.Parameters.AddWithValue("$offset",(long)(revisionPage-1)*limit);
        var revisionHistory=new JsonArray();
        using(var reader=await revisionsQuery.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while(await reader.ReadAsync(ct).ConfigureAwait(false))
                revisionHistory.Add(new JsonObject
                {
                    ["date"]=reader.GetString(0),["before"]=reader.GetString(1),
                    ["after"]=reader.GetString(2),
                    ["reason"]=reader.IsDBNull(3)?null:reader.GetString(3),
                    ["executedAtUtc"]=reader.GetString(4),
                    ["authorizationMethod"]=reader.IsDBNull(5)?null:reader.GetString(5),
                    ["executor"]=reader.GetString(6)
                });
        // Historical site memberships are independently effective-dated and
        // keyed by the same immutable PersonId, never by mutable personnel no.
        using var assignmentCount=conn.CreateCommand();
        assignmentCount.CommandText="SELECT COUNT(*) FROM PersonAssignments WHERE PersonId=$id;";
        assignmentCount.Parameters.AddWithValue("$id",personId);
        long totalSites=Convert.ToInt64(await assignmentCount.ExecuteScalarAsync(ct).ConfigureAwait(false));
        using var assignments=conn.CreateCommand();
        assignments.CommandText="""
            SELECT a.SiteId,a.ValidFromLocalDate,a.ValidToLocalDate,
                   COALESCE((
                      SELECT n.VisibleNameSnapshot FROM SiteNameHistory n
                      WHERE n.SiteId=a.SiteId AND n.ValidFromLocalDate<=a.ValidFromLocalDate
                        AND (n.ValidToLocalDate IS NULL OR n.ValidToLocalDate>a.ValidFromLocalDate)
                      ORDER BY n.ValidFromLocalDate DESC LIMIT 1),
                      CASE WHEN a.SiteId='SITE-UNSPECIFIED' THEN 'غير محدد'
                           ELSE 'غير موثق تاريخياً' END)
            FROM PersonAssignments a WHERE a.PersonId=$id
            ORDER BY a.ValidFromLocalDate DESC,a.AssignmentId DESC
            LIMIT $size OFFSET $offset;
            """;
        assignments.Parameters.AddWithValue("$id",personId);
        assignments.Parameters.AddWithValue("$size",limit);
        assignments.Parameters.AddWithValue("$offset",(long)(sitePage-1)*limit);
        var sites=new JsonArray();
        using(var r=await assignments.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while(await r.ReadAsync(ct).ConfigureAwait(false))
                sites.Add(new JsonObject{
                    ["siteId"]=r.GetString(0),["from"]=r.GetString(1),
                    ["to"]=r.IsDBNull(2)?null:r.GetString(2),
                    ["siteName"]=r.GetString(3)
                });
        return BridgeHelpers.Ok(new JsonObject
        {
            ["person"]=identity,["dateFrom"]=filters.From,["dateTo"]=filters.To,
            ["siteId"]=filters.SiteId,["statusId"]=filters.StatusId,
            ["movementType"]=filters.MovementType,
            ["siteHistory"]=sites,["siteTotal"]=totalSites,
            ["sitePage"]=sitePage,["siteHasNext"]=(long)sitePage*limit<totalSites,
            ["statusTotals"]=totals,["monthlyTotals"]=monthly,
            ["attendance"]=daily,["attendanceTotal"]=totalDays,
            ["attendancePage"]=attendPage,["attendanceHasNext"]=(long)attendPage*limit<totalDays,
            ["identityHistory"]=names,["identityTotal"]=totalIdentities,
            ["identityPage"]=identityPage,
            ["identityHasNext"]=(long)identityPage*limit<totalIdentities,
            ["movements"]=moves,["movementTotal"]=totalMovements,
            ["movementPage"]=movementPage,
            ["movementHasNext"]=(long)movementPage*limit<totalMovements,
            ["revisions"]=revisionHistory,["revisionTotal"]=totalRevisions,
            ["revisionPage"]=revisionPage,
            ["revisionHasNext"]=(long)revisionPage*limit<totalRevisions,
            ["audit"]=events,["auditTotal"]=audits,["auditPage"]=auditPage,
            ["auditHasNext"]=(long)auditPage*limit<audits,
            ["pageSize"]=limit
        });
    }
}
