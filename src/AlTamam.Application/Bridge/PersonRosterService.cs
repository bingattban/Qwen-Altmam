using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Contracts.Events;
using AlTamam.Domain.Enums;
using AlTamam.Domain.Policies;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Phase 05 — PersonRosterService: real person master-data CRUD over the encrypted store.
/// Search/query/select by stable PersonId with «الرقم (ع)» textual normalization
/// (<see cref="ArabicTextNormalizer"/>), uniqueness enforced at the storage boundary, safely
/// paged/filtered results, effective-dated identity history, and preview/confirm archive semantics.
/// All mutations run inside one exact write transaction; every action returns canonical
/// <see cref="BridgeResponse"/> envelopes with UI-invalidation events on committed changes.
/// </summary>
public sealed class PersonRosterService
{
    private const int MaxPageSize = 200;
    private static readonly string[] ValidDatePattern = ["yyyy-MM-dd"];

    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;

    public PersonRosterService(AlTamamDbContext database, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _db = database;
        _time = timeProvider ?? TimeProvider.System;
    }

    // ------------------------------------------------------------------ 05.01 search / query / select

    /// <summary>Paged, filtered person listing keyed by stable PersonId (never by name or الرقم (ع)).</summary>
    public async Task<BridgeResponse> QueryPersonsAsync(JsonObject payload, CancellationToken ct)
    {
        string term = ReadString(payload, "term");
        string siteId = ReadString(payload, "siteId");
        int organizationalStatus = ReadInt(payload, "organizationalStatus", 0);
        bool includeArchived = ReadBool(payload, "includeArchived", false);
        int page = Math.Max(ReadInt(payload, "page", 1), 1);
        int pageSize = Math.Clamp(ReadInt(payload, "pageSize", 25), 1, MaxPageSize);

        var where = new List<string>();
        using var connection = _db.CreateOpenConnection();
        using var countCmd = connection.CreateCommand();
        var parameters = new List<(string Name, object Value)>();

        if (!string.IsNullOrWhiteSpace(term))
        {
            string like = "%" + EscapeLike(ArabicTextNormalizer.NormalizeForSearch(term)) + "%";
            where.Add("(p.NormalizedName LIKE $term ESCAPE '\\' OR p.NormalizedServiceNumberA LIKE $term ESCAPE '\\')");
            parameters.Add(("$term", like));
        }
        if (!string.IsNullOrWhiteSpace(siteId))
        {
            where.Add(@"EXISTS (SELECT 1 FROM PersonAssignments a WHERE a.PersonId = p.PersonId
                                AND a.SiteId = $site AND a.ValidToLocalDate IS NULL)");
            parameters.Add(("$site", siteId));
        }
        if (organizationalStatus >= 1 && organizationalStatus <= 4)
        {
            where.Add("p.OrganizationalStatus = $org");
            parameters.Add(("$org", organizationalStatus));
            // The active roster must also respect ActiveFlag; explicit archived
            // queries for exited persons remain possible by status or flag.
            if (organizationalStatus == 1) where.Add("p.ActiveFlag = 1");
        }
        else if (!includeArchived)
        {
            // Default person pickers and strength rosters must never surface
            // externally transferred, separated or archived personnel.
            where.Add("p.OrganizationalStatus = 1 AND p.ActiveFlag = 1");
        }

        string whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        countCmd.CommandText = $"SELECT COUNT(*) FROM Persons p {whereSql};";
        foreach (var (name, value) in parameters) countCmd.Parameters.Add(name, SqliteType.Text).Value = value;
        long total = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

        using var listCmd = connection.CreateCommand();
        listCmd.CommandText = $"""
            SELECT p.PersonId, p.CurrentVisibleName, p.CurrentServiceNumberA, p.OrganizationalStatus,
                   p.ActiveFlag, p.RowVersion,
                   COALESCE((SELECT s.VisibleName FROM PersonAssignments a JOIN Sites s ON s.SiteId = a.SiteId
                             WHERE a.PersonId = p.PersonId AND a.ValidToLocalDate IS NULL), 'غير محدد')
            FROM Persons p {whereSql}
            ORDER BY p.CurrentVisibleName, p.PersonId
            LIMIT $limit OFFSET $offset;
            """;
        foreach (var (name, value) in parameters) listCmd.Parameters.Add(name, SqliteType.Text).Value = value;
        listCmd.Parameters.Add("$limit", SqliteType.Integer).Value = pageSize;
        listCmd.Parameters.Add("$offset", SqliteType.Integer).Value = (page - 1) * pageSize;

        var items = new JsonArray();
        using var reader = await listCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new JsonObject
            {
                ["personId"] = reader.GetString(0),
                ["displayName"] = reader.GetString(1),
                ["serviceNumberA"] = reader.GetString(2),
                ["organizationalStatus"] = reader.GetInt32(3),
                ["active"] = Convert.ToInt32(reader.GetValue(4)) == 1,
                ["revision"] = reader.GetInt64(5),
                ["siteName"] = reader.GetString(6)
            });
        }

        return BridgeHelpers.Ok(new JsonObject
        {
            ["items"] = items,
            ["count"] = items.Count,
            ["total"] = total,
            ["page"] = page,
            ["pageSize"] = pageSize,
            ["hasNext"] = (long)page * pageSize < total
        });
    }

    /// <summary>Full selectable profile of one person addressed strictly by stable PersonId.</summary>
    public async Task<BridgeResponse> GetPersonSummaryAsync(JsonObject payload, CancellationToken ct)
    {
        string personId = ReadRequiredString(payload, "personId");
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT CurrentVisibleName, NormalizedName, CurrentServiceNumberA, OrganizationalStatus,
                   ActiveFlag, RowVersion, FirstRecordedLocalDate
            FROM Persons WHERE PersonId = $id;
            """;
        cmd.Parameters.Add("$id", SqliteType.Text).Value = personId;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return BridgeHelpers.Error(BridgeErrorCodes.MissingData, "الفرد المطلوب غير موجود بنفس المعرف الثابت.");

        var summary = new JsonObject
        {
            ["personId"] = personId,
            ["displayName"] = reader.GetString(0),
            ["normalizedDisplayName"] = reader.GetString(1),
            ["serviceNumberA"] = reader.GetString(2),
            ["organizationalStatus"] = reader.GetInt32(3),
            ["active"] = Convert.ToInt32(reader.GetValue(4)) == 1,
            ["revision"] = reader.GetInt64(5),
            ["firstRecordedLocalDate"] = reader.GetString(6)
        };
        reader.Close();

        using var histCmd = connection.CreateCommand();
        histCmd.CommandText = """
            SELECT NameSnapshot, ServiceNumberASnapshot, ValidFromLocalDate, ValidToLocalDate, Reason, RecordedAtUtc
            FROM PersonIdentityHistory WHERE PersonId = $id ORDER BY ValidFromLocalDate DESC LIMIT 50;
            """;
        histCmd.Parameters.Add("$id", SqliteType.Text).Value = personId;
        var history = new JsonArray();
        using var histReader = await histCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await histReader.ReadAsync(ct).ConfigureAwait(false))
            history.Add(new JsonObject
            {
                ["name"] = histReader.GetString(0),
                ["serviceNumberA"] = histReader.GetString(1),
                ["validFrom"] = histReader.GetString(2),
                ["validTo"] = histReader.IsDBNull(3) ? null : histReader.GetString(3),
                ["reason"] = histReader.GetString(4),
                ["recordedAtUtc"] = histReader.GetString(5)
            });
        summary["identityHistory"] = history;
        return BridgeHelpers.Ok(summary);
    }

    // ------------------------------------------------------------------ 05.02 identity create/edit

    /// <summary>Creates a person with a permanent stable PersonId; duplicate normalized الرقم (ع) is rejected.</summary>
    public async Task<BridgeResponse> CreatePersonAsync(string userId, string username, JsonObject payload, CancellationToken ct)
    {
        string visibleName = ReadRequiredString(payload, "visibleName").Trim();
        string serviceNumber = ReadRequiredString(payload, "serviceNumberA").Trim();
        string firstRecorded = ReadString(payload, "firstRecordedLocalDate");
        string effectiveDate = string.IsNullOrWhiteSpace(firstRecorded) ? Today() : firstRecorded;
        if (!IsValidIsoDate(effectiveDate))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "تاريخ التسجيل الأول غير صالح (ISO yyyy-MM-dd).");
        if (visibleName.Length < 3)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "الاسم الكامل قصير جداً.");

        string normalizedName = ArabicTextNormalizer.NormalizeForSearch(visibleName);
        if (!ServiceNumberPolicy.TryCanonicalize(serviceNumber, out serviceNumber))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "الرقم (ع) يجب أن يتكون من أربعة أرقام على الأقل؛ لا تستخدم الرقم التسلسلي العادي.");
        string normalizedNumber = serviceNumber;
        string siteIdRequested = ReadString(payload, "siteId").Trim();
        string siteName = ReadString(payload, "siteName").Trim();
        if (siteName.Length > 100 || siteName.Any(char.IsControl) ||
            siteIdRequested.Length > 100 || siteIdRequested.Any(char.IsControl))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "الموقع المختار غير صالح.");

        string personId = "PRN-" + Guid.NewGuid().ToString("N");
        DateTimeOffset nowUtc = _time.GetUtcNow();
        return await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            var (replay,receipt)=await MutationIdempotency.StartAsync(
                connection,tx,"CreatePerson",userId,payload,ct).ConfigureAwait(false);
            if(replay is not null)return replay;
            using var dup = connection.CreateCommand();
            dup.Transaction = tx;
            dup.CommandText = """
                SELECT PersonId, CurrentVisibleName FROM Persons
                WHERE NormalizedServiceNumberA = $n AND ActiveFlag = 1 LIMIT 1;
                """;
            dup.Parameters.Add("$n", SqliteType.Text).Value = normalizedNumber;
            using(var activeConflict = await dup.ExecuteReaderAsync(ct).ConfigureAwait(false))
                if(await activeConflict.ReadAsync(ct).ConfigureAwait(false))
                    return BridgeHelpers.Error(BridgeErrorCodes.DuplicateServiceNumber,
                        $"«الرقم (ع)» مستخدم بالفعل للفرد النشط «{activeConflict.GetString(1)}» (PersonId: {activeConflict.GetString(0)})؛ لم يُحفظ أي فرد جديد.");

            // The ONLY exception to an archived-number conflict is a verified,
            // explicitly approved reuse of ONE dismissed person's number for a
            // DIFFERENT human. The new person receives a fresh PersonId and NO
            // records or relationships are copied from the dismissed person.
            // Other archived collisions fail closed. Every archived candidate
            // must match the actor-bound, one-use preview under this transaction.
            string resolutionToken=ReadString(payload,"identityResolutionToken").Trim();
            string decision=ReadString(payload,"identityDecision").Trim();
            var matches=new List<(string Id,string Name,string Number,string NormalizedNumber,long Revision,int Org)>();
            using(var archived = connection.CreateCommand())
            {
                archived.Transaction = tx;
                archived.CommandText = """
                    SELECT p.PersonId,p.CurrentVisibleName,p.CurrentServiceNumberA,
                           p.NormalizedServiceNumberA,p.RowVersion,p.OrganizationalStatus
                    FROM Persons p
                    WHERE p.ActiveFlag=0 AND p.OrganizationalStatus IN (2,3,4)
                      AND (p.NormalizedServiceNumberA=$number OR p.NormalizedName=$name)
                    ORDER BY p.PersonId LIMIT 51;
                    """;
                archived.Parameters.AddWithValue("$number",normalizedNumber);
                archived.Parameters.AddWithValue("$name",normalizedName);
                using var candidates=await archived.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while(await candidates.ReadAsync(ct).ConfigureAwait(false))
                    matches.Add((candidates.GetString(0),candidates.GetString(1),
                        candidates.GetString(2),candidates.GetString(3),
                        candidates.GetInt64(4),candidates.GetInt32(5)));
            }
            if(matches.Count>50)
                return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                    "توجد هويات كثيرة متطابقة في الأرشيف؛ يجب حل التعارض قبل الإضافة.");
            var numberMatches=matches.Where(x=>x.NormalizedNumber==normalizedNumber).ToArray();
            bool replaceDismissed=decision=="replaceDismissed";
            string dismissedPersonId=ReadString(payload,"dismissedPersonId").Trim();
            if(numberMatches.Length>0 &&
                (!replaceDismissed || numberMatches.Length!=1 ||
                 numberMatches[0].Org!=3 || dismissedPersonId!=numberMatches[0].Id))
                return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                    "الرقم (ع) موجود في الأرشيف؛ الاستبدال مسموح فقط لفرد مفصول واحد بعد معاينة وموافقة صريحة، وليس للمنقول خارجياً.");
            if(replaceDismissed && numberMatches.Length!=1)
                return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                    "لا توجد هوية مفصولة واحدة مطابقة للرقم (ع) يمكن الموافقة على استبدالها.");
            if(matches.Count>0)
            {
                if((decision!="different"&&!replaceDismissed)||resolutionToken.Length!=64)
                    return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                        "يوجد تطابق اسم في الأرشيف؛ يلزم إثبات أن الفرد الجديد شخص مختلف من نافذة التحقق من الهوية.");
                using var resolve=connection.CreateCommand();
                resolve.Transaction=tx;
                resolve.CommandText="""
                    SELECT PayloadCanonicalJson FROM PreviewTokens
                    WHERE Token=$token AND Operation=$op AND ActorUserId=$actor
                      AND ConsumedFlag=0 AND ExpiresAtUtc>$now;
                    """;
                resolve.Parameters.AddWithValue("$token",resolutionToken);
                resolve.Parameters.AddWithValue("$op",ArchivedReturnService.PreviewOperation);
                resolve.Parameters.AddWithValue("$actor",userId);
                resolve.Parameters.AddWithValue("$now",nowUtc.ToString("O"));
                string? raw=await resolve.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                JsonObject? plan=raw is null?null:JsonNode.Parse(raw) as JsonObject;
                if(plan is null||ReadString(plan,"businessDate")!=Today()||
                   ReadString(plan,"visibleName")!=visibleName||
                   ReadString(plan,"serviceNumberA")!=normalizedNumber||
                   ReadString(plan,"siteId")!=(siteIdRequested.Length>0?siteIdRequested:"SITE-UNSPECIFIED")||
                   plan["candidates"] is not JsonArray prior||prior.Count!=matches.Count)
                    return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                        "انتهت معاينة اختلاف الهوية أو تغيّرت البيانات؛ أعد البحث عن الاسم.");
                foreach(var current in matches)
                {
                    var oldCandidate=prior.OfType<JsonObject>().FirstOrDefault(
                        x=>ReadString(x,"personId")==current.Id);
                    if(oldCandidate is null || ReadLong(oldCandidate,"revision",-1)!=current.Revision ||
                       ReadString(oldCandidate,"name")!=current.Name||
                       ReadString(oldCandidate,"serviceNumberA")!=current.Number)
                        return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                            "تغيّر مرشح في الأرشيف؛ أعد قرار اختلاف الهوية من جديد.");
                }
                // An active person, external transfer, later return, or changed
                // dismissal invalidates the preview even if a UI sends a forged
                // replaceDismissed decision.
                if(replaceDismissed)
                {
                    var selected=prior.OfType<JsonObject>().SingleOrDefault(
                        x=>ReadString(x,"personId")==dismissedPersonId);
                    if(selected is null ||
                       ReadString(selected,"exitType")!="فصل" ||
                       !ReadBool(selected,"numberMatch",false) ||
                       ReadInt(selected,"organizationalStatus",0)!=3)
                        return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                            "الرقم (ع) لا يطابق حالة فصل مؤكدة في المعاينة؛ أعد التحقق.");
                }
                long? used=await BridgePreviewTokens.ConsumeAsync(_db,_time,connection,tx,
                    resolutionToken,ArchivedReturnService.PreviewOperation,userId,0,ct)
                    .ConfigureAwait(false);
                if(used!=0)return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                    "معاينة الهوية مستخدمة أو منتهية.");
            }
            else if(resolutionToken.Length>0||decision.Length>0)
                return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                    "لم يعد هناك تعارض في الأرشيف؛ أعد إضافة الفرد دون بيانات معاينة قديمة.");

            // Add Person may choose EXISTING active sites only. Import Excel
            // has its own explicit site-creation process and remains unchanged.
            string siteId = "SITE-UNSPECIFIED";
            if(siteIdRequested.Length>0 || (siteName.Length>0 && siteName!="غير محدد"))
            {
                using var find = connection.CreateCommand();
                find.Transaction = tx;
                if(siteIdRequested.Length>0)
                {
                    find.CommandText="SELECT SiteId FROM Sites WHERE SiteId=$site AND ActiveFlag=1;";
                    find.Parameters.AddWithValue("$site",siteIdRequested);
                }
                else
                {
                    find.CommandText="SELECT SiteId FROM Sites WHERE NormalizedName=$name AND ActiveFlag=1 LIMIT 1;";
                    find.Parameters.AddWithValue("$name",ArabicTextNormalizer.NormalizeForSearch(siteName));
                }
                siteId = await find.ExecuteScalarAsync(ct).ConfigureAwait(false) as string ?? "";
                if(siteId.Length==0)
                    return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                        "الموقع غير موجود أو غير نشط. اختر موقعاً مسجلاً من شاشة المواقع، أو «غير محدد».");
            }
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO Persons (PersonId, CurrentVisibleName, NormalizedName, CurrentServiceNumberA,
                                     NormalizedServiceNumberA, FirstRecordedLocalDate, FirstRecordedAtUtc,
                                     OrganizationalStatus, ActiveFlag, RowVersion)
                VALUES ($id, $name, $normName, $num, $normNum, $date, $now, 1, 1, 1);
                """;
            insert.Parameters.Add("$id", SqliteType.Text).Value = personId;
            insert.Parameters.Add("$name", SqliteType.Text).Value = visibleName;
            insert.Parameters.Add("$normName", SqliteType.Text).Value = normalizedName;
            insert.Parameters.Add("$num", SqliteType.Text).Value = serviceNumber;
            insert.Parameters.Add("$normNum", SqliteType.Text).Value = normalizedNumber;
            insert.Parameters.Add("$date", SqliteType.Text).Value = effectiveDate;
            insert.Parameters.Add("$now", SqliteType.Text).Value = nowUtc.ToString("O");
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var snapshot = connection.CreateCommand();
            snapshot.Transaction = tx;
            snapshot.CommandText = """
                INSERT INTO PersonIdentityHistory (PersonId, NameSnapshot, ServiceNumberASnapshot,
                                                   ValidFromLocalDate, ValidToLocalDate, ChangedByUserId, RecordedAtUtc, Reason)
                VALUES ($id, $name, $num, $date, NULL, $user, $now, 'إنشاء أولي في سجل القوة');
                """;
            snapshot.Parameters.Add("$id", SqliteType.Text).Value = personId;
            snapshot.Parameters.Add("$name", SqliteType.Text).Value = visibleName;
            snapshot.Parameters.Add("$num", SqliteType.Text).Value = serviceNumber;
            snapshot.Parameters.Add("$date", SqliteType.Text).Value = effectiveDate;
            snapshot.Parameters.Add("$user", SqliteType.Text).Value = userId;
            snapshot.Parameters.Add("$now", SqliteType.Text).Value = nowUtc.ToString("O");
            await snapshot.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            using (var assign = connection.CreateCommand())
            {
                assign.Transaction = tx;
                assign.CommandText = """
                    INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                        ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                    VALUES($id,$person,$site,$day,NULL,NULL,$now);
                    """;
                assign.Parameters.AddWithValue("$id", "ASG-" + Guid.NewGuid().ToString("N"));
                assign.Parameters.AddWithValue("$person", personId);
                assign.Parameters.AddWithValue("$site", siteId);
                assign.Parameters.AddWithValue("$day", effectiveDate);
                assign.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
                await assign.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await BridgeAuditWriter.WriteAsync(connection, tx, _time,
                operationCode: "PERSON_CREATE", operationArabicName: "إنشاء فرد",
                actorUserId: userId, actorUsername: username, actorDisplayName: visibleName,
                targetEntityType: "Person", targetEntityId: personId, targetDisplay: $"{visibleName} ({serviceNumber})",
                oldValueJson: null, newValueJson: new JsonObject { ["personId"] = personId, ["name"] = visibleName,
                    ["serviceNumberA"] = serviceNumber,
                    ["independentFromArchivedIdentity"] = matches.Count>0,
                    ["reusedDismissedNumber"] = replaceDismissed }.ToJsonString(),
                description: $"أُنشئ سجل الفرد «{visibleName}» بالمعرف الثابت {personId}.",
                reason: null, effectiveLocalDate: effectiveDate, correlationId: ct.CorrelationId(), ct).ConfigureAwait(false);

            if(replaceDismissed)
            {
                // Keep the mapping ONLY in a separate number-reuse audit event.
                // It is not a Person relationship and is never included in either
                // person's lifetime report (which filters strictly on PersonId).
                await BridgeAuditWriter.WriteAsync(connection, tx, _time,
                    operationCode: "DISMISSED_NUMBER_REUSED",
                    operationArabicName: "إعادة استخدام رقم فرد مفصول لشخص جديد مستقل",
                    actorUserId: userId, actorUsername: username, actorDisplayName: null,
                    targetEntityType: "PersonnelNumberAssignment",
                    targetEntityId: normalizedNumber, targetDisplay: normalizedNumber,
                    oldValueJson: new JsonObject { ["dismissedPersonId"] = dismissedPersonId }.ToJsonString(),
                    newValueJson: new JsonObject { ["newPersonId"] = personId }.ToJsonString(),
                    description: "أُعيد استخدام الرقم (ع) بعد الفصل لهوية جديدة مستقلة دون نقل أي سجلات تاريخية.",
                    reason: "موافقة صريحة بعد معاينة هوية مفصولة", effectiveLocalDate: effectiveDate,
                    correlationId: ct.CorrelationId(), ct).ConfigureAwait(false);
            }
            var outcome=new JsonObject { ["personId"] = personId, ["siteId"] = siteId, ["revision"] = 1L };
            await MutationIdempotency.StoreAsync(connection,tx,receipt,outcome,_time,ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(
                outcome,
                revision: 1,
                events: BridgeHelpers.InvalidationEvent(UiInvalidationEvents.AttendanceCommitted, effectiveDate, [personId]));
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Preview of an identity change: shows old→new snapshots and conflict checks without writing.</summary>
    public async Task<BridgeResponse> PreviewPersonIdentityUpdateAsync(JsonObject payload, CancellationToken ct)
    {
        string personId = ReadRequiredString(payload, "personId");
        long baseRevision = ReadLong(payload, "baseRevision", 0);
        string newName = ReadRequiredString(payload, "visibleName").Trim();
        string newNumber = ReadRequiredString(payload, "serviceNumberA").Trim();
        // The SQLCipher boundary stores ASCII canonical text, never the raw
        // Arabic-Indic/Persian glyphs entered by the operator. Preserve zeros.
        if (!ServiceNumberPolicy.TryCanonicalize(newNumber, out string canonicalNumber))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "الرقم (ع) يجب أن يتكون من 4 إلى 70 رقماً دون حروف أو فراغات.");
        newNumber = canonicalNumber;
        string effectiveDate = ReadEffectiveDate(payload);

        (PersonRow? Row, BridgeResponse? Failure) loaded = await LoadPersonAsync(personId, ct).ConfigureAwait(false);
        if (loaded.Failure is not null) return loaded.Failure;
        if (loaded.Row!.RowVersion != baseRevision)
            return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale, "نسخة بيانات الفرد تغيّرت؛ أعد التحميل قبل المعاينة.");

        string normalizedNumber = ArabicTextNormalizer.NormalizeForSearch(newNumber);
        using var connection = _db.CreateOpenConnection();
        using var conflict = connection.CreateCommand();
        conflict.CommandText = """
            SELECT PersonId, CurrentVisibleName FROM Persons
            WHERE NormalizedServiceNumberA = $n AND ActiveFlag = 1 AND PersonId <> $id LIMIT 1;
            """;
        conflict.Parameters.Add("$n", SqliteType.Text).Value = normalizedNumber;
        conflict.Parameters.Add("$id", SqliteType.Text).Value = personId;
        using var reader = await conflict.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
            return BridgeHelpers.Error(BridgeErrorCodes.DuplicateServiceNumber,
                $"«الرقم (ع)» المطبَّع مطابق لسجل الفرد «{reader.GetString(1)}»؛ عدّل أحدهما.");
        reader.Close();
        if(normalizedNumber!=loaded.Row.NormalizedNumber)
        {
            using var archivedNumber=connection.CreateCommand();
            archivedNumber.CommandText="""
                SELECT EXISTS(SELECT 1 FROM Persons
                  WHERE NormalizedServiceNumberA=$number AND ActiveFlag=0
                    AND OrganizationalStatus IN(2,3,4));
                """;
            archivedNumber.Parameters.AddWithValue("$number",normalizedNumber);
            if(Convert.ToInt32(await archivedNumber.ExecuteScalarAsync(ct).ConfigureAwait(false))!=0)
                return BridgeHelpers.Error(BridgeErrorCodes.DuplicateServiceNumber,
                    "الرقم (ع) مؤرشف لشخص سابق؛ استخدم إضافة فرد مع تحقق الهوية ولا تتجاوزه بتعديل الرقم.");
        }

        return BridgeHelpers.Ok(new JsonObject
        {
            ["personId"] = personId,
            ["oldName"] = loaded.Row.VisibleName,
            ["newName"] = newName,
            ["oldServiceNumberA"] = loaded.Row.ServiceNumberA,
            ["newServiceNumberA"] = newNumber,
            ["effectiveLocalDate"] = effectiveDate,
            ["baseRevision"] = baseRevision,
            ["willCloseOldSnapshot"] = true,
            ["previewToken"] = BridgePreviewTokens.Issue(_db, _time, "UpdatePersonIdentity",
                actorUserId: ReadString(payload, "actorHint"), payload, baseRevision, ct)
        });
    }

    /// <summary>Applies an identity edit with an effective date, preserving the previous snapshot immutably.</summary>
    public async Task<BridgeResponse> UpdatePersonIdentityAsync(string userId, string username, JsonObject payload, CancellationToken ct)
    {
        string personId = ReadRequiredString(payload, "personId");
        long expectedRevision = ReadLong(payload, "expectedRevision", ReadLong(payload, "baseRevision", 0));
        string newName = ReadRequiredString(payload, "visibleName").Trim();
        string newNumber = ReadRequiredString(payload, "serviceNumberA").Trim();
        // The SQLCipher boundary stores ASCII canonical text, never the raw
        // Arabic-Indic/Persian glyphs entered by the operator. Preserve zeros.
        if (!ServiceNumberPolicy.TryCanonicalize(newNumber, out string canonicalNumber))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "الرقم (ع) يجب أن يتكون من 4 إلى 70 رقماً دون حروف أو فراغات.");
        newNumber = canonicalNumber;
        string effectiveDate = ReadEffectiveDate(payload);
        string reason = ReadString(payload, "reason") is { Length: > 0 } r ? r : "تحديث هوية بأمر المسؤول.";

        // A completed reply can be lost after the row version changed. Return its
        // durable result *before* rejecting the retry as stale. This is a
        // read-only receipt preflight under the same serialized write gate.
        if (ReadString(payload, "operationId").Length != 0)
        {
            var prior = await _db.ExecuteInWriteTransactionAsync(async (c, tx) =>
            {
                var (replayed, _) = await MutationIdempotency.StartAsync(
                    c, tx, "UpdatePersonIdentity", userId, payload, ct).ConfigureAwait(false);
                return replayed;
            }, ct).ConfigureAwait(false);
            if (prior is not null) return prior;
        }

        (PersonRow? Row, BridgeResponse? Failure) loaded = await LoadPersonAsync(personId, ct).ConfigureAwait(false);
        if (loaded.Failure is not null) return loaded.Failure;
        if (loaded.Row!.RowVersion != expectedRevision)
            return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale, "نسخة بيانات الفرد قديمة؛ أعد التحميل قبل الحفظ.");
        if (string.Equals(ArabicTextNormalizer.NormalizeForSearch(newName), loaded.Row.NormalizedName, StringComparison.Ordinal)
            && string.Equals(ArabicTextNormalizer.NormalizeForSearch(newNumber), loaded.Row.NormalizedNumber, StringComparison.Ordinal))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "لا تغيير في الهوية لحفظه.");

        string normalizedNumber = ArabicTextNormalizer.NormalizeForSearch(newNumber);
        DateTimeOffset nowUtc = _time.GetUtcNow();
        return await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            var (replayed, receipt) = await MutationIdempotency.StartAsync(
                connection, tx, "UpdatePersonIdentity", userId, payload, ct).ConfigureAwait(false);
            if (replayed is not null) return replayed;
            using var conflict = connection.CreateCommand();
            conflict.Transaction = tx;
            conflict.CommandText = """
                SELECT CurrentVisibleName FROM Persons
                WHERE NormalizedServiceNumberA = $n AND ActiveFlag = 1 AND PersonId <> $id LIMIT 1;
                """;
            conflict.Parameters.Add("$n", SqliteType.Text).Value = normalizedNumber;
            conflict.Parameters.Add("$id", SqliteType.Text).Value = personId;
            object? clash = await conflict.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (clash is not null and not DBNull)
                return BridgeHelpers.Error(BridgeErrorCodes.DuplicateServiceNumber,
                    $"«الرقم (ع)» المطبَّع مطابق للفرد النشط «{clash}» بعد التدقيق داخل المعاملة.");

            // A routine identity edit cannot bypass the explicit dismissed-number
            // replacement workflow. It must not change the owner of an archived
            // number without a separate person's first admission.
            if(normalizedNumber!=loaded.Row.NormalizedNumber)
            {
                using var archivedNumber=connection.CreateCommand();
                archivedNumber.Transaction=tx;
                archivedNumber.CommandText="""
                    SELECT EXISTS(SELECT 1 FROM Persons
                      WHERE NormalizedServiceNumberA=$number AND ActiveFlag=0
                        AND OrganizationalStatus IN(2,3,4));
                    """;
                archivedNumber.Parameters.AddWithValue("$number",normalizedNumber);
                if(Convert.ToInt32(await archivedNumber.ExecuteScalarAsync(ct).ConfigureAwait(false))!=0)
                    return BridgeHelpers.Error(BridgeErrorCodes.DuplicateServiceNumber,
                        "الرقم (ع) محفوظ لهوية مؤرشفة؛ لا يمكن إعادة استخدامه عبر تعديل فرد نشط.");
            }

            // A single calendar day cannot hold two identity snapshots: refuse a
            // same-day/backdated edit before any write, rather than relying on a
            // database constraint exception that would become an opaque UI error.
            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = tx;
                existing.CommandText = """
                    SELECT ValidFromLocalDate FROM PersonIdentityHistory
                    WHERE PersonId = $id AND ValidToLocalDate IS NULL
                    ORDER BY ValidFromLocalDate DESC, HistoryId DESC LIMIT 1;
                    """;
                existing.Parameters.AddWithValue("$id", personId);
                string? from = await existing.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                if (from is not null && string.CompareOrdinal(effectiveDate, from) <= 0)
                    return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                        "لا يمكن تغيير هوية الفرد مرتين في اليوم نفسه أو بتاريخ يسبق هويته الحالية.");
            }

            // Update the concurrency-guarded person row FIRST, so a stale revision
            // cannot leave a committed history closure or orphaned identity snapshot.
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE Persons SET CurrentVisibleName = $name, NormalizedName = $normName,
                                   CurrentServiceNumberA = $num, NormalizedServiceNumberA = $normNum,
                                   RowVersion = RowVersion + 1
                WHERE PersonId = $id AND RowVersion = $rev;
                """;
            update.Parameters.Add("$name", SqliteType.Text).Value = newName;
            update.Parameters.Add("$normName", SqliteType.Text).Value = ArabicTextNormalizer.NormalizeForSearch(newName);
            update.Parameters.Add("$num", SqliteType.Text).Value = newNumber;
            update.Parameters.Add("$normNum", SqliteType.Text).Value = normalizedNumber;
            update.Parameters.Add("$id", SqliteType.Text).Value = personId;
            update.Parameters.Add("$rev", SqliteType.Integer).Value = expectedRevision;
            int touched = await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (touched == 0)
                return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale, "تعذر التحديث: نسخة الصف لم تعد مطابقة.");


            // Close the open identity snapshot exactly at the effective date (exclusive end).
            using var close = connection.CreateCommand();
            close.Transaction = tx;
            close.CommandText = """
                UPDATE PersonIdentityHistory SET ValidToLocalDate = $date
                WHERE PersonId = $id AND ValidToLocalDate IS NULL AND ValidFromLocalDate < $date;
                """;
            close.Parameters.Add("$date", SqliteType.Text).Value = effectiveDate;
            close.Parameters.Add("$id", SqliteType.Text).Value = personId;
            await close.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var snapshot = connection.CreateCommand();
            snapshot.Transaction = tx;
            snapshot.CommandText = """
                INSERT INTO PersonIdentityHistory (PersonId, NameSnapshot, ServiceNumberASnapshot,
                                                   ValidFromLocalDate, ValidToLocalDate, ChangedByUserId, RecordedAtUtc, Reason)
                VALUES ($id, $name, $num, $date, NULL, $user, $now, $reason);
                """;
            snapshot.Parameters.Add("$id", SqliteType.Text).Value = personId;
            snapshot.Parameters.Add("$name", SqliteType.Text).Value = newName;
            snapshot.Parameters.Add("$num", SqliteType.Text).Value = newNumber;
            snapshot.Parameters.Add("$date", SqliteType.Text).Value = effectiveDate;
            snapshot.Parameters.Add("$user", SqliteType.Text).Value = userId;
            snapshot.Parameters.Add("$now", SqliteType.Text).Value = nowUtc.ToString("O");
            snapshot.Parameters.Add("$reason", SqliteType.Text).Value = reason;
            await snapshot.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await BridgeAuditWriter.WriteAsync(connection, tx, _time,
                operationCode: "PERSON_IDENTITY_UPDATE", operationArabicName: "تعديل هوية فرد",
                actorUserId: userId, actorUsername: username, actorDisplayName: loaded.Row.VisibleName,
                targetEntityType: "Person", targetEntityId: personId, targetDisplay: $"{newName} ({newNumber})",
                oldValueJson: new JsonObject { ["name"] = loaded.Row.VisibleName, ["serviceNumberA"] = loaded.Row.ServiceNumberA }.ToJsonString(),
                newValueJson: new JsonObject { ["name"] = newName, ["serviceNumberA"] = newNumber }.ToJsonString(),
                description: $"عُدّلت هوية الفرد اعتباراً من {effectiveDate} مع حفظ لقطة الهوية السابقة.",
                reason: reason, effectiveLocalDate: effectiveDate, correlationId: ct.CorrelationId(), ct).ConfigureAwait(false);

            var outcome = new JsonObject {
                ["personId"] = personId, ["revision"] = expectedRevision + 1 };
            await MutationIdempotency.StoreAsync(connection, tx, receipt, outcome, _time, ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(outcome, revision: expectedRevision + 1,
                events: BridgeHelpers.InvalidationEvent(UiInvalidationEvents.PersonIdentityUpdated, effectiveDate, [personId]));
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ 05.03 archive from active force

    /// <summary>Preview of archiving persons out of the active force: impacts, assignments, blockers.</summary>
    public async Task<BridgeResponse> PreviewArchivePersonsAsync(JsonObject payload, string actorUserId, CancellationToken ct)
    {
        IReadOnlyList<string> ids = ReadStringArray(payload, "personIds");
        string effectiveDate = ReadEffectiveDate(payload);
        if (effectiveDate != Today())
            return BridgeHelpers.Error(BridgeErrorCodes.HistoricDayLocked,
                "لا يمكن إجراء أرشفة بأثر رجعي أو تغيير اليوم المغلق؛ اختر التاريخ المحلي الحالي.");
        if (ids.Count == 0 || ids.Any(string.IsNullOrWhiteSpace) ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "اختر أفراداً موجودين دون تكرار للمعاينة.");

        using var connection = _db.CreateOpenConnection();
        var candidates = new JsonArray();
        var expectedVersions = new JsonArray();
        long baseRevisionSum = 0;
        // Parameterized batched reads avoid one database round-trip per person.
        // Reconstruct the preview in the caller's original order, including its
        // revisions and first-missing-person error semantics.
        var details = new Dictionary<string,
            (string Name, string Number, int Org, bool Active, long Revision, string Site, long Days)>(
                StringComparer.Ordinal);
        const int batchSize = 400; // Safely below SQLite's legacy parameter limit.
        for (int offset = 0; offset < ids.Count; offset += batchSize)
        {
            string[] batch = ids.Skip(offset).Take(batchSize).ToArray();
            using var cmd = connection.CreateCommand();
            string[] placeholders = new string[batch.Length];
            for (int i = 0; i < batch.Length; i++)
            {
                placeholders[i] = "$person" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                cmd.Parameters.Add(placeholders[i], SqliteType.Text).Value = batch[i];
            }
            cmd.CommandText = $"""
                SELECT p.PersonId, p.CurrentVisibleName, p.CurrentServiceNumberA,
                       p.OrganizationalStatus, p.ActiveFlag, p.RowVersion,
                       COALESCE((SELECT s.VisibleName
                                 FROM PersonAssignments a JOIN Sites s ON s.SiteId=a.SiteId
                                 WHERE a.PersonId=p.PersonId AND a.ValidToLocalDate IS NULL), 'غير محدد'),
                       (SELECT COUNT(*) FROM AttendanceRecords r WHERE r.PersonId=p.PersonId)
                FROM Persons p WHERE p.PersonId IN ({string.Join(",", placeholders)});
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                details.Add(reader.GetString(0),
                    (reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                     Convert.ToInt32(reader.GetValue(4)) == 1, reader.GetInt64(5),
                     reader.GetString(6), reader.GetInt64(7)));
        }
        foreach (string id in ids)
        {
            if (!details.TryGetValue(id, out var row))
                return BridgeHelpers.Error(BridgeErrorCodes.MissingData, $"الفرد {id} غير موجود — التمهيد غير صالح.");
            baseRevisionSum += row.Revision;
            expectedVersions.Add(new JsonObject { ["personId"] = id, ["rowVersion"] = row.Revision });
            candidates.Add(new JsonObject
            {
                ["personId"] = id,
                ["displayName"] = row.Name,
                ["serviceNumberA"] = row.Number,
                ["organizationalStatus"] = row.Org,
                ["currentSiteName"] = row.Site,
                ["attendanceDays"] = row.Days,
                ["blocker"] = !row.Active ? "محفوظ مسبقاً" : row.Org == 4 ? "مؤرشف سابقاً" : null
            });
        }

        return BridgeHelpers.Ok(new JsonObject
        {
            ["candidates"] = candidates,
            ["effectiveLocalDate"] = effectiveDate,
            ["baseRevision"] = baseRevisionSum,
            ["reversible"] = true,
            ["expectedVersions"] = expectedVersions.DeepClone(),
            ["previewToken"] = BridgePreviewTokens.Issue(_db, _time, "ArchivePersons",
                actorUserId: actorUserId,
                new JsonObject { ["personIds"] = payload["personIds"]?.DeepClone(),
                    ["effectiveLocalDate"] = effectiveDate,
                    ["expectedVersions"] = expectedVersions.DeepClone() }, baseRevisionSum, ct)
        });
    }

    /// <summary>
    /// Preview-token-bound atomic removal from the CURRENT roster. It closes
    /// memberships prospectively; all attendance, movement and audit history survives.
    /// Preflight checks every member before the first database mutation.
    /// </summary>
    public async Task<BridgeResponse> ArchivePersonsAsync(string userId, string username, JsonObject payload, CancellationToken ct)
    {
        IReadOnlyList<string> ids = ReadStringArray(payload, "personIds");
        string effectiveDate = ReadEffectiveDate(payload);
        if (effectiveDate != Today())
            return BridgeHelpers.Error(BridgeErrorCodes.HistoricDayLocked,
                "إزالة فرد من القوة تتم لليوم المحلي الحالي فقط، ولا تغيّر الأشهر المغلقة.");
        string reason = ReadString(payload, "reason") is { Length: > 0 } r ? r : "إخراج من القوة الفعالة بقرار محفوظ.";
        string token = ReadString(payload, "previewToken");
        if (ids.Count == 0 || ids.Any(string.IsNullOrWhiteSpace) ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "اختر قائمة أفراد صحيحة دون تكرار.");
        if (token.Length != 64 || token.Any(c => !Uri.IsHexDigit(c)))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "معاينة حديثة صادرة لنفس المستخدم مطلوبة قبل الأرشفة.");
        long expected = ReadLong(payload, "expectedRevision", -1);
        long baseRevision = ReadLong(payload, "baseRevision", -1);
        if (expected < 1 || expected != baseRevision)
            return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                "نسخة المعاينة قديمة؛ أعد معاينة جميع الأفراد قبل التنفيذ.");

        return await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            var (replay, receipt) = await MutationIdempotency.StartAsync(
                connection, tx, "ArchivePersons", userId, payload, ct).ConfigureAwait(false);
            if (replay is not null) return replay;
            // Do not trust the client-supplied ID list: it must match the immutable
            // preview issued to the SAME authenticated actor.
            var memberExpectedVersions = new Dictionary<string,long>(StringComparer.Ordinal);
            using (var preview = connection.CreateCommand())
            {
                preview.Transaction = tx;
                preview.CommandText = """
                    SELECT PayloadCanonicalJson FROM PreviewTokens
                    WHERE Token=$token AND Operation='ArchivePersons' AND ActorUserId=$actor;
                    """;
                preview.Parameters.AddWithValue("$token", token);
                preview.Parameters.AddWithValue("$actor", userId);
                var raw = await preview.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                if (raw is null)
                    return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                        "انتهت المعاينة أو لم تصدر لهذا الحساب.");
                var canonical = JsonNode.Parse(raw) as JsonObject;
                if (canonical is null ||
                    ReadEffectiveDate(canonical) != effectiveDate ||
                    !ReadStringArray(canonical, "personIds").SequenceEqual(ids, StringComparer.Ordinal))
                    return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                        "تغيّرت قائمة الأفراد أو تاريخ العملية بعد المعاينة.");
                if (canonical["expectedVersions"] is not JsonArray versions ||
                    versions.Count != ids.Count)
                    return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                        "لا تتضمن المعاينة إصدارات جميع الأفراد.");
                foreach (JsonNode? entry in versions)
                {
                    var item = entry as JsonObject;
                    string memberId = item is null ? "" : ReadString(item, "personId");
                    long memberVersion = item is null ? -1 : ReadLong(item, "rowVersion", -1);
                    if (!ids.Contains(memberId) || memberVersion < 1 ||
                        !memberExpectedVersions.TryAdd(memberId, memberVersion))
                        return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                            "قائمة إصدارات الأفراد في المعاينة غير صالحة.");
                }
            }

            var people = new List<(string Id,string Name,int Org,int Active,long Revision)>();
            long sum = 0;
            foreach (string id in ids)
            {
                using var find = connection.CreateCommand();
                find.Transaction = tx;
                find.CommandText = """
                    SELECT CurrentVisibleName,OrganizationalStatus,ActiveFlag,RowVersion,
                      EXISTS(SELECT 1 FROM PersonAssignments a
                        WHERE a.PersonId=Persons.PersonId AND a.ValidToLocalDate IS NULL
                          AND a.ValidFromLocalDate < $date)
                    FROM Persons WHERE PersonId=$id;
                    """;
                find.Parameters.AddWithValue("$id", id);
                find.Parameters.AddWithValue("$date", effectiveDate);
                using var reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    return BridgeHelpers.Error(BridgeErrorCodes.MissingData, "أحد الأفراد لم يعد موجودًا؛ أُلغيت الدفعة كاملة.");
                string name = reader.GetString(0);
                int org = reader.GetInt32(1), active = reader.GetInt32(2);
                long version = reader.GetInt64(3);
                bool eligible = reader.GetInt32(4) == 1;
                reader.Close();
                if (org != 1 || active != 1 || !eligible ||
                    !memberExpectedVersions.TryGetValue(id, out long memberExpected) ||
                    version != memberExpected)
                    return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                        $"تغير انتساب أو إصدار الفرد «{name}» بعد المعاينة؛ لم تُجرَ أي تغييرات.");
                people.Add((id,name,org,active,version));
                sum += version;
            }
            if (sum != baseRevision)
                return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                    "تغيّرت إصدارات الأفراد بعد المعاينة؛ لم تُجرَ أي تغييرات.");
            long? used = await BridgePreviewTokens.ConsumeAsync(_db, _time, connection, tx,
                token, "ArchivePersons", userId, baseRevision, ct).ConfigureAwait(false);
            if (used != baseRevision)
                return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                    "المعاينة منتهية أو سبق اعتمادها؛ لا يمكن تطبيق العملية مرتين.");

            var archived = new JsonArray();
            foreach (var person in people)
            {
                using (var close = connection.CreateCommand())
                {
                    close.Transaction = tx;
                    close.CommandText = """
                        UPDATE PersonAssignments SET ValidToLocalDate=$date
                        WHERE PersonId=$id AND ValidToLocalDate IS NULL
                          AND ValidFromLocalDate < $date;
                        """;
                    close.Parameters.AddWithValue("$date", effectiveDate);
                    close.Parameters.AddWithValue("$id", person.Id);
                    if (await close.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                        throw new InvalidOperationException("Concurrent archive assignment change.");
                }
                using (var flip = connection.CreateCommand())
                {
                    flip.Transaction = tx;
                    flip.CommandText = """
                        UPDATE Persons SET OrganizationalStatus=4,ActiveFlag=0,RowVersion=RowVersion+1
                        WHERE PersonId=$id AND RowVersion=$rev
                          AND OrganizationalStatus=1 AND ActiveFlag=1;
                        """;
                    flip.Parameters.AddWithValue("$id", person.Id);
                    flip.Parameters.AddWithValue("$rev", person.Revision);
                    if (await flip.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                        throw new InvalidOperationException("Concurrent archive personnel change.");
                }
                archived.Add(person.Id);
                await BridgeAuditWriter.WriteAsync(connection, tx, _time,
                    operationCode: "PERSON_ARCHIVE", operationArabicName: "أرشفة فرد من القوة",
                    actorUserId: userId, actorUsername: username, actorDisplayName: null,
                    targetEntityType: "Person", targetEntityId: person.Id, targetDisplay: person.Name,
                    oldValueJson: new JsonObject { ["organizationalStatus"] = person.Org,
                        ["active"] = true, ["rowVersion"] = person.Revision }.ToJsonString(),
                    newValueJson: new JsonObject { ["organizationalStatus"] = 4,
                        ["active"] = false, ["rowVersion"] = person.Revision + 1 }.ToJsonString(),
                    description: $"أُخرج «{person.Name}» من القوة الفعالة اعتباراً من {effectiveDate} مع حفظ التاريخ.",
                    reason: reason, effectiveLocalDate: effectiveDate,
                    correlationId: token, ct).ConfigureAwait(false);
                await AccountLifecycleService.SuspendAccountsForPersonnelExitAsync(
                    connection,tx,person.Id,"PERSON_ARCHIVE",userId,username,_time,
                    effectiveDate,ct).ConfigureAwait(false);
            }
            var outcome = new JsonObject { ["archivedPersonIds"] = archived,
                ["effectiveLocalDate"] = effectiveDate, ["operationId"] = token };
            await MutationIdempotency.StoreAsync(connection, tx, receipt, outcome, _time, ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(outcome, revision: baseRevision + people.Count,
                events: BridgeHelpers.InvalidationEvent(UiInvalidationEvents.PersonnelArchived, effectiveDate, ids));
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-activates a roster member without reopening a CLOSED historical assignment.
    /// The new assignment begins at the requested effective date and preserves the
    /// archived period as an actual gap in the person's immutable timeline.
    /// </summary>
    public async Task<BridgeResponse> RestoreArchivedPersonAsync(string userId, string username, JsonObject payload, CancellationToken ct)
    {
        string personId = ReadRequiredString(payload, "personId");
        string effectiveDate = ReadEffectiveDate(payload);
        if (effectiveDate != Today())
            return BridgeHelpers.Error(BridgeErrorCodes.HistoricDayLocked,
                "العودة إلى القوة تبدأ من تاريخ اليوم المحلي فقط، دون إعادة كتابة التحضير السابق.");
        long expectedVersion = ReadLong(payload, "expectedRevision", -1);
        if (expectedVersion < 1)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "إصدار الفرد السابق مطلوب للاسترجاع؛ حدّث بيانات الفرد أولاً.");
        return await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            var (replay, receipt) = await MutationIdempotency.StartAsync(
                connection, tx, "RestoreArchivedPerson", userId, payload, ct).ConfigureAwait(false);
            if (replay is not null) return replay;
            using var find = connection.CreateCommand();
            find.Transaction = tx;
            find.CommandText = """
                SELECT CurrentVisibleName, OrganizationalStatus, ActiveFlag, RowVersion
                FROM Persons WHERE PersonId=$id;
                """;
            find.Parameters.AddWithValue("$id", personId);
            using var reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return BridgeHelpers.Error(BridgeErrorCodes.MissingData, "الفرد غير موجود.");
            string name = reader.GetString(0);
            int org = reader.GetInt32(1);
            bool active = reader.GetInt32(2) == 1;
            long revision = reader.GetInt64(3);
            reader.Close();
            if (org != 4 || active || revision != expectedVersion)
                return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                    "الفرد ليس مؤرشفاً بهذا الإصدار؛ حدّث الشاشة وأعد المحاولة.");

            // Validate the entire history before mutating anything. A return must be
            // after the most recent exclusive end, not a rewrite of an earlier interval.
            string destinationSite = "SITE-UNSPECIFIED";
            using (var latest = connection.CreateCommand())
            {
                latest.Transaction = tx;
                latest.CommandText = """
                    SELECT a.SiteId, a.ValidToLocalDate, s.ActiveFlag
                    FROM PersonAssignments a JOIN Sites s ON s.SiteId=a.SiteId
                    WHERE a.PersonId=$id
                    ORDER BY a.ValidFromLocalDate DESC LIMIT 1;
                    """;
                latest.Parameters.AddWithValue("$id", personId);
                using var prior = await latest.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await prior.ReadAsync(ct).ConfigureAwait(false))
                {
                    string? lastEnd = prior.IsDBNull(1) ? null : prior.GetString(1);
                    if (lastEnd is null || string.CompareOrdinal(effectiveDate, lastEnd) < 0)
                        return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                            "تاريخ العودة يتداخل مع انتساب سابق أو لم تُغلق الفترة القديمة.");
                    if (prior.GetInt32(2) == 1)
                        destinationSite = prior.GetString(0);
                }
            }

            string assignmentId = "ASG-" + Guid.NewGuid().ToString("N");
            string now = _time.GetUtcNow().ToString("O");
            using (var update = connection.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE Persons SET OrganizationalStatus=1, ActiveFlag=1,
                        RowVersion=RowVersion+1
                    WHERE PersonId=$id AND RowVersion=$expected
                        AND OrganizationalStatus=4 AND ActiveFlag=0;
                    """;
                update.Parameters.AddWithValue("$id", personId);
                update.Parameters.AddWithValue("$expected", expectedVersion);
                if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                    return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale, "تغيّر إصدار الفرد أثناء الاسترجاع.");
            }
            using (var newAssignment = connection.CreateCommand())
            {
                newAssignment.Transaction = tx;
                newAssignment.CommandText = """
                    INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                        ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                    VALUES($assignment,$person,$site,$day,NULL,NULL,$now);
                    """;
                newAssignment.Parameters.AddWithValue("$assignment", assignmentId);
                newAssignment.Parameters.AddWithValue("$person", personId);
                newAssignment.Parameters.AddWithValue("$site", destinationSite);
                newAssignment.Parameters.AddWithValue("$day", effectiveDate);
                newAssignment.Parameters.AddWithValue("$now", now);
                await newAssignment.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await BridgeAuditWriter.WriteAsync(connection, tx, _time,
                operationCode: "PERSON_RESTORE", operationArabicName: "استرجاع فرد مؤرشف",
                actorUserId: userId, actorUsername: username, actorDisplayName: name,
                targetEntityType: "Person", targetEntityId: personId, targetDisplay: name,
                oldValueJson: new JsonObject { ["organizationalStatus"] = 4, ["rowVersion"] = revision }.ToJsonString(),
                newValueJson: new JsonObject { ["organizationalStatus"] = 1,
                    ["rowVersion"] = revision + 1, ["newAssignmentId"] = assignmentId,
                    ["siteId"] = destinationSite }.ToJsonString(),
                description: $"استُرجع «{name}» إلى القوة الفعالة اعتباراً من {effectiveDate} بفترة انتساب جديدة تحفظ فجوة الأرشفة.",
                reason: ReadString(payload, "reason"), effectiveLocalDate: effectiveDate,
                correlationId: ct.CorrelationId(), ct).ConfigureAwait(false);

            var outcome = new JsonObject { ["personId"] = personId, ["restored"] = true,
                ["assignmentId"] = assignmentId, ["siteId"] = destinationSite,
                ["revision"] = revision + 1 };
            await MutationIdempotency.StoreAsync(connection, tx, receipt, outcome, _time, ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(outcome,
                events: BridgeHelpers.InvalidationEvent(UiInvalidationEvents.PersonnelArchived, effectiveDate, [personId]));
        }, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- helpers

    private sealed record PersonRow(string VisibleName, string NormalizedName, string ServiceNumberA, string NormalizedNumber, long RowVersion);

    private async Task<(PersonRow? Row, BridgeResponse? Failure)> LoadPersonAsync(string personId, CancellationToken ct)
    {
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT CurrentVisibleName, NormalizedName, CurrentServiceNumberA, NormalizedServiceNumberA, RowVersion
            FROM Persons WHERE PersonId = $id;
            """;
        cmd.Parameters.Add("$id", SqliteType.Text).Value = personId;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return (null, BridgeHelpers.Error(BridgeErrorCodes.MissingData, "الفرد المطلوب غير موجود بنفس المعرف الثابت."));
        var row = new PersonRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4));
        return (row, null);
    }

    private string Today() => DateOnly.FromDateTime(_time.GetLocalNow().DateTime).ToString("yyyy-MM-dd");

    private string ReadEffectiveDate(JsonObject payload)
    {
        string date = ReadString(payload, "effectiveLocalDate");
        return string.IsNullOrWhiteSpace(date) || !IsValidIsoDate(date) ? Today() : date;
    }

    internal static bool IsValidIsoDate(string value) =>
        DateOnly.TryParseExact(value, ValidDatePattern, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);

    internal static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    internal static string ReadString(JsonObject payload, string key) =>
        payload.TryGetPropertyValue(key, out JsonNode? node) && node?.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>() : "";

    private static string ReadRequiredString(JsonObject payload, string key)
    {
        string value = ReadString(payload, key).Trim();
        if (value.Length == 0)
            throw new BridgeProtocolException(BridgeErrorCodes.ValidationError, $"الحقل '{key}' مطلوب.");
        return value;
    }

    internal static int ReadInt(JsonObject payload, string key, int fallback)
    {
        if (!payload.TryGetPropertyValue(key, out JsonNode? node) || node is null) return fallback;
        try { return node.GetValue<int>(); } catch { return fallback; }
    }

    internal static long ReadLong(JsonObject payload, string key, long fallback)
    {
        if (!payload.TryGetPropertyValue(key, out JsonNode? node) || node is null) return fallback;
        try { return node.GetValue<long>(); }
        catch
        {
            // In-memory JSON tests can hold Int32 even though WebView JSON numbers
            // deserialize as Int64. Both must represent the same expected revision.
            try { return node.GetValue<int>(); } catch { return fallback; }
        }
    }

    internal static bool ReadBool(JsonObject payload, string key, bool fallback)
    {
        if (!payload.TryGetPropertyValue(key, out JsonNode? node) || node is null) return fallback;
        try { return node.GetValue<bool>(); } catch { return fallback; }
    }

    internal static IReadOnlyList<string> ReadStringArray(JsonObject payload, string key)
    {
        if (payload.TryGetPropertyValue(key, out JsonNode? node) && node is JsonArray arr)
        {
            var list = new List<string>(arr.Count);
            foreach (JsonNode? item in arr)
                if (item?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    list.Add(item.GetValue<string>());
            return list;
        }
        return Array.Empty<string>();
    }
}
