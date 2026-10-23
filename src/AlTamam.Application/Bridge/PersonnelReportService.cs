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
    private static JsonArray MakeArray(IEnumerable<JsonObject> xs)
    { var result=new JsonArray();foreach(var value in xs)result.Add(value);return result; }

    public async Task<BridgeResponse> ReportAsync(JsonObject? payload,CancellationToken ct)
    {
        string personId=Text(payload,"personId");
        if(personId.Length<3||personId.Length>128)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,"حدد الفرد من كشف البرنامج.");
        int attendPage=Page(payload,"attendancePage"),auditPage=Page(payload,"auditPage");
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
            GROUP BY StatusId,StatusNameSnapshot
            ORDER BY SUM(1) DESC,StatusNameSnapshot;
            """;
        status.Parameters.AddWithValue("$id",personId);
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
            GROUP BY SUBSTR(AttendanceLocalDate,1,7),StatusId,StatusNameSnapshot
            ORDER BY 1 DESC,3;
            """;
        months.Parameters.AddWithValue("$id",personId);
        var monthly=new JsonArray();
        using(var r=await months.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while(await r.ReadAsync(ct).ConfigureAwait(false))
                monthly.Add(new JsonObject
                {
                    ["period"]=r.GetString(0),["statusId"]=r.GetString(1),
                    ["statusName"]=r.GetString(2),["count"]=r.GetInt64(3)
                });

        using var attendanceCount=conn.CreateCommand();
        attendanceCount.CommandText="SELECT COUNT(*) FROM AttendanceRecords WHERE PersonId=$id;";
        attendanceCount.Parameters.AddWithValue("$id",personId);
        long totalDays=Convert.ToInt64(await attendanceCount.ExecuteScalarAsync(ct).ConfigureAwait(false));
        using var attendance=conn.CreateCommand();
        attendance.CommandText="""
            SELECT AttendanceLocalDate,StatusNameSnapshot,StatusSymbolSnapshot,
                   SiteNameSnapshot,Source,RevisionNumber,CommittedAtUtc
            FROM AttendanceRecords WHERE PersonId=$id
            ORDER BY AttendanceLocalDate DESC LIMIT $size OFFSET $offset;
            """;
        attendance.Parameters.AddWithValue("$id",personId);
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
                    ["committedAtUtc"]=r.GetString(6)
                });

        using var histories=conn.CreateCommand();
        histories.CommandText="""
            SELECT NameSnapshot,ServiceNumberASnapshot,ValidFromLocalDate,
                   ValidToLocalDate,ChangedByUserId,Reason
            FROM PersonIdentityHistory WHERE PersonId=$id
            ORDER BY ValidFromLocalDate DESC,HistoryId DESC LIMIT 200;
            """;
        histories.Parameters.AddWithValue("$id",personId);
        var names=new JsonArray();
        using(var r=await histories.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while(await r.ReadAsync(ct).ConfigureAwait(false))
                names.Add(new JsonObject
                {
                    ["name"]=r.GetString(0),["serviceNumberA"]=r.GetString(1),
                    ["from"]=r.GetString(2),["to"]=r.IsDBNull(3)?null:r.GetString(3),
                    ["actorUserId"]=r.GetString(4),["reason"]=r.GetString(5)
                });

        using var movements=conn.CreateCommand();
        movements.CommandText="""
            SELECT MovementType,SourceSiteNameSnapshot,
                   COALESCE(DestinationSiteNameSnapshot,DestinationExternalName,''),
                   SeparationReason,EffectiveLocalDate,CommittedAtUtc,ActorUserId
            FROM PersonnelMovements WHERE PersonId=$id
            ORDER BY EffectiveLocalDate DESC,CommittedAtUtc DESC LIMIT 200;
            """;
        movements.Parameters.AddWithValue("$id",personId);
        var moves=new JsonArray();
        using(var r=await movements.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while(await r.ReadAsync(ct).ConfigureAwait(false))
                moves.Add(new JsonObject
                {
                    ["type"]=r.GetInt32(0)==1?"نقل داخلي":
                        r.GetInt32(0)==2?"نقل خارجي":"فصل",
                    ["source"]=r.GetString(1),["destination"]=r.GetString(2),
                    ["reason"]=r.IsDBNull(3)?null:r.GetString(3),
                    ["date"]=r.GetString(4),["committedAtUtc"]=r.GetString(5),
                    ["actorUserId"]=r.GetString(6)
                });

        using var auditCount=conn.CreateCommand();
        auditCount.CommandText="""
            SELECT COUNT(*) FROM AuditEvents
            WHERE TargetEntityType='Person' AND TargetEntityId=$id;
            """;
        auditCount.Parameters.AddWithValue("$id",personId);
        long audits=Convert.ToInt64(await auditCount.ExecuteScalarAsync(ct).ConfigureAwait(false));
        using var audit=conn.CreateCommand();
        audit.CommandText="""
            SELECT OperationArabicName,ActorUsernameSnapshot,
                   COALESCE(ActorRosterFullNameSnapshot,''),
                   EffectiveLocalDate,ExecutedAtUtc,DefaultArabicDescription,Reason
            FROM AuditEvents
            WHERE TargetEntityType='Person' AND TargetEntityId=$id
            ORDER BY ExecutedAtUtc DESC,AuditEventId DESC LIMIT $size OFFSET $offset;
            """;
        audit.Parameters.AddWithValue("$id",personId);
        audit.Parameters.AddWithValue("$size",limit);
        audit.Parameters.AddWithValue("$offset",(long)(auditPage-1)*limit);
        var events=new JsonArray();
        using(var r=await audit.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while(await r.ReadAsync(ct).ConfigureAwait(false))
                events.Add(new JsonObject
                {
                    ["operation"]=r.GetString(0),["username"]=r.GetString(1),
                    ["fullName"]=r.GetString(2),["effectiveDate"]=r.GetString(3),
                    ["executedAtUtc"]=r.GetString(4),["description"]=r.GetString(5),
                    ["reason"]=r.IsDBNull(6)?null:r.GetString(6)
                });
        return BridgeHelpers.Ok(new JsonObject
        {
            ["person"]=identity,["statusTotals"]=totals,["monthlyTotals"]=monthly,
            ["attendance"]=daily,["attendanceTotal"]=totalDays,
            ["attendancePage"]=attendPage,["attendanceHasNext"]=(long)attendPage*limit<totalDays,
            ["identityHistory"]=names,["movements"]=moves,
            ["audit"]=events,["auditTotal"]=audits,["auditPage"]=auditPage,
            ["auditHasNext"]=(long)auditPage*limit<audits,
            ["pageSize"]=limit
        });
    }
}
