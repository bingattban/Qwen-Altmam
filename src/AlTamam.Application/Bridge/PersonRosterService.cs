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
        }
        else if (!includeArchived)
        {
            where.Add("p.OrganizationalStatus <> 4");
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
        string siteName = ReadString(payload, "siteName").Trim();
        if (siteName.Length == 0) siteName = "غير محدد";
        if (siteName.Length > 100 || (siteName != "غير محدد" && siteName.Length < 2)
            || siteName.Any(char.IsControl))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "اسم الموقع غير صالح.");

        string personId = "PRN-" + Guid.NewGuid().ToString("N");
        DateTimeOffset nowUtc = _time.GetUtcNow();
        return await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            using var dup = connection.CreateCommand();
            dup.Transaction = tx;
            dup.CommandText = "SELECT EXISTS(SELECT 1 FROM Persons WHERE NormalizedServiceNumberA = $n AND ActiveFlag = 1);";
            dup.Parameters.Add("$n", SqliteType.Text).Value = normalizedNumber;
            if (Convert.ToInt32(await dup.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 1)
                return BridgeHelpers.Error(BridgeErrorCodes.DuplicateServiceNumber,
                    "«الرقم (ع)» مستخدم بالفعل لفرد نشط بعد التطبيع؛ لا يمكن تكراره.");

            string siteId = "SITE-UNSPECIFIED";
            if (siteName != "غير محدد")
            {
                string normalizedSite = ArabicTextNormalizer.NormalizeForSearch(siteName);
                using var find = connection.CreateCommand();
                find.Transaction = tx;
                find.CommandText = """
                    SELECT SiteId FROM Sites WHERE NormalizedName=$name AND ActiveFlag=1 LIMIT 1;
                    """;
                find.Parameters.AddWithValue("$name", normalizedSite);
                siteId = await find.ExecuteScalarAsync(ct).ConfigureAwait(false) as string ?? "";
                if (siteId.Length == 0)
                {
                    siteId = "SITE-" + Guid.NewGuid().ToString("N");
                    using var create = connection.CreateCommand();
                    create.Transaction = tx;
                    create.CommandText = """
                        INSERT INTO Sites(SiteId,VisibleName,NormalizedName,IsSystemSite,
                                          ActiveFlag,CreatedAtUtc,RowVersion)
                        VALUES($id,$name,$normalized,0,1,$now,1);
                        INSERT INTO SiteNameHistory(SiteId,VisibleNameSnapshot,ValidFromLocalDate,
                                                    ValidToLocalDate,ChangedByUserId,RecordedAtUtc)
                        VALUES($id,$name,$day,NULL,$actor,$now);
                        """;
                    create.Parameters.AddWithValue("$id", siteId);
                    create.Parameters.AddWithValue("$name", siteName);
                    create.Parameters.AddWithValue("$normalized", normalizedSite);
                    create.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
                    create.Parameters.AddWithValue("$day", effectiveDate);
                    create.Parameters.AddWithValue("$actor", userId);
                    await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    await BridgeAuditWriter.WriteAsync(connection, tx, _time,
                        "SITE_CREATE", "إنشاء موقع تلقائياً", userId, username, username,
                        "Site", siteId, siteName, null,
                        new JsonObject { ["siteId"] = siteId, ["name"] = siteName }.ToJsonString(),
                        "تسجيل موقع غير موجود عند إضافة فرد", null, effectiveDate,
                        ct.CorrelationId(), ct).ConfigureAwait(false);
                }
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
                oldValueJson: null, newValueJson: new JsonObject { ["personId"] = personId, ["name"] = visibleName, ["serviceNumberA"] = serviceNumber }.ToJsonString(),
                description: $"أُنشئ سجل الفرد «{visibleName}» بالمعرف الثابت {personId}.",
                reason: null, effectiveLocalDate: effectiveDate, correlationId: ct.CorrelationId(), ct).ConfigureAwait(false);

            return BridgeHelpers.Ok(
                new JsonObject { ["personId"] = personId, ["siteId"] = siteId, ["revision"] = 1L },
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
        string effectiveDate = ReadEffectiveDate(payload);
        string reason = ReadString(payload, "reason") is { Length: > 0 } r ? r : "تحديث هوية بأمر المسؤول.";

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

            return BridgeHelpers.Ok(
                new JsonObject { ["personId"] = personId, ["revision"] = expectedRevision + 1 },
                revision: expectedRevision + 1,
                events: BridgeHelpers.InvalidationEvent(UiInvalidationEvents.PersonIdentityUpdated, effectiveDate, [personId]));
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ 05.03 archive from active force

    /// <summary>Preview of archiving persons out of the active force: impacts, assignments, blockers.</summary>
    public async Task<BridgeResponse> PreviewArchivePersonsAsync(JsonObject payload, CancellationToken ct)
    {
        IReadOnlyList<string> ids = ReadStringArray(payload, "personIds");
        string effectiveDate = ReadEffectiveDate(payload);
        if (ids.Count == 0)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "اختر فرداً واحداً على الأقل للتمهيد.");

        using var connection = _db.CreateOpenConnection();
        var candidates = new JsonArray();
        long baseRevisionSum = 0;
        foreach (string id in ids)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT CurrentVisibleName, CurrentServiceNumberA, OrganizationalStatus, ActiveFlag, RowVersion,
                       COALESCE((SELECT s.VisibleName FROM PersonAssignments a JOIN Sites s ON s.SiteId = a.SiteId
                                 WHERE a.PersonId = p.PersonId AND a.ValidToLocalDate IS NULL), 'غير محدد'),
                       (SELECT COUNT(*) FROM AttendanceRecords r WHERE r.PersonId = p.PersonId)
                FROM Persons p WHERE PersonId = $id;
                """;
            cmd.Parameters.Add("$id", SqliteType.Text).Value = id;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return BridgeHelpers.Error(BridgeErrorCodes.MissingData, $"الفرد {id} غير موجود — التمهيد غير صالح.");
            var org = reader.GetInt32(2);
            var active = Convert.ToInt32(reader.GetValue(3)) == 1;
            baseRevisionSum += reader.GetInt64(4);
            candidates.Add(new JsonObject
            {
                ["personId"] = id,
                ["displayName"] = reader.GetString(0),
                ["serviceNumberA"] = reader.GetString(1),
                ["organizationalStatus"] = org,
                ["currentSiteName"] = reader.GetString(5),
                ["attendanceDays"] = reader.GetInt64(6),
                ["blocker"] = !active ? "محفوظ مسبقاً" : org == 4 ? "مؤرشف سابقاً" : null
            });
        }

        return BridgeHelpers.Ok(new JsonObject
        {
            ["candidates"] = candidates,
            ["effectiveLocalDate"] = effectiveDate,
            ["baseRevision"] = baseRevisionSum,
            ["reversible"] = true,
            ["previewToken"] = BridgePreviewTokens.Issue(_db, _time, "ArchivePersons",
                actorUserId: ReadString(payload, "actorHint"), payload, baseRevisionSum, ct)
        });
    }

    /// <summary>Authorized archive: closes open assignments, flips to ArchivedFromForce, keeps full history.</summary>
    public async Task<BridgeResponse> ArchivePersonsAsync(string userId, string username, JsonObject payload, CancellationToken ct)
    {
        IReadOnlyList<string> ids = ReadStringArray(payload, "personIds");
        string effectiveDate = ReadEffectiveDate(payload);
        string reason = ReadString(payload, "reason") is { Length: > 0 } r ? r : "إخراج من القوة الفعالة بقرار محفوظ.";
        if (ids.Count == 0)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "لم تُحدَّد أي أفراد.");

        long expected = ReadLong(payload, "expectedRevision", 0);
        long baseRevision = ReadLong(payload, "baseRevision", 0);
        if (expected != baseRevision)
            return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale, "نسخة المعاينة قديمة؛ أعد التمهيد قبل التنفيذ.");

        return await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            var archived = new JsonArray();
            long sum = 0;
            foreach (string id in ids)
            {
                using var find = connection.CreateCommand();
                find.Transaction = tx;
                find.CommandText = "SELECT CurrentVisibleName, OrganizationalStatus, ActiveFlag, RowVersion FROM Persons WHERE PersonId = $id;";
                find.Parameters.Add("$id", SqliteType.Text).Value = id;
                using var reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    return BridgeHelpers.Error(BridgeErrorCodes.MissingData, $"الفرد {id} لم يعد موجوداً — أُلغيت العملية كاملة.");
                string name = reader.GetString(0);
                int org = reader.GetInt32(1);
                int active = Convert.ToInt32(reader.GetValue(2));
                long rowVersion = reader.GetInt64(3);
                reader.Close();
                if (org == 4 || active == 0)
                    return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale, $"الفرد «{name}» مؤرشف بالفعل؛ أعد التمهيد.");
                sum += rowVersion;

                using var close = connection.CreateCommand();
                close.Transaction = tx;
                close.CommandText = """
                    UPDATE PersonAssignments SET ValidToLocalDate = $date
                    WHERE PersonId = $id AND ValidToLocalDate IS NULL AND ValidFromLocalDate < $date;
                    """;
                close.Parameters.Add("$date", SqliteType.Text).Value = effectiveDate;
                close.Parameters.Add("$id", SqliteType.Text).Value = id;
                await close.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                using var flip = connection.CreateCommand();
                flip.Transaction = tx;
                flip.CommandText = """
                    UPDATE Persons SET OrganizationalStatus = 4, ActiveFlag = 0, RowVersion = RowVersion + 1
                    WHERE PersonId = $id;
                    """;
                flip.Parameters.Add("$id", SqliteType.Text).Value = id;
                await flip.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                archived.Add(id);
                await BridgeAuditWriter.WriteAsync(connection, tx, _time,
                    operationCode: "PERSON_ARCHIVE", operationArabicName: "أرشفة فرد من القوة",
                    actorUserId: userId, actorUsername: username, actorDisplayName: name,
                    targetEntityType: "Person", targetEntityId: id, targetDisplay: name,
                    oldValueJson: new JsonObject { ["organizationalStatus"] = org, ["active"] = active == 1 }.ToJsonString(),
                    newValueJson: new JsonObject { ["organizationalStatus"] = 4, ["active"] = false }.ToJsonString(),
                    description: $"أُخرج «{name}» من القوة الفعالة اعتباراً من {effectiveDate} مع إبقاء السجل قابلاً للاسترجاع.",
                    reason: reason, effectiveLocalDate: effectiveDate, correlationId: ct.CorrelationId(), ct).ConfigureAwait(false);
            }

            if (sum != baseRevision)
                return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale, "تغيّرت نسخة البيانات أثناء التنفيذ؛ لُفّظت المعاملة كاملة.");

            return BridgeHelpers.Ok(
                new JsonObject { ["archivedPersonIds"] = archived, ["effectiveLocalDate"] = effectiveDate },
                revision: baseRevision + archived.Count,
                events: BridgeHelpers.InvalidationEvent(UiInvalidationEvents.PersonnelArchived, effectiveDate, ids));
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Reverses an archive decision: restores Active status and reopens the latest assignment window.</summary>
    public async Task<BridgeResponse> RestoreArchivedPersonAsync(string userId, string username, JsonObject payload, CancellationToken ct)
    {
        string personId = ReadRequiredString(payload, "personId");
        string effectiveDate = ReadEffectiveDate(payload);
        DateTimeOffset nowUtc = _time.GetUtcNow();
        return await _db.ExecuteInWriteTransactionAsync(async (connection, tx) =>
        {
            using var find = connection.CreateCommand();
            find.Transaction = tx;
            find.CommandText = "SELECT CurrentVisibleName, OrganizationalStatus FROM Persons WHERE PersonId = $id;";
            find.Parameters.Add("$id", SqliteType.Text).Value = personId;
            using var reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return BridgeHelpers.Error(BridgeErrorCodes.MissingData, "الفرد غير موجود.");
            string name = reader.GetString(0);
            int org = reader.GetInt32(1);
            reader.Close();
            if (org != 4)
                return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "الفرد ليس في حالة أرشفة ليُسترجع.");

            using var restore = connection.CreateCommand();
            restore.Transaction = tx;
            restore.CommandText = "UPDATE Persons SET OrganizationalStatus = 1, ActiveFlag = 1, RowVersion = RowVersion + 1 WHERE PersonId = $id;";
            restore.Parameters.Add("$id", SqliteType.Text).Value = personId;
            await restore.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            using var reopen = connection.CreateCommand();
            reopen.Transaction = tx;
            reopen.CommandText = """
                UPDATE PersonAssignments SET ValidToLocalDate = NULL
                WHERE AssignmentId = (SELECT AssignmentId FROM PersonAssignments
                                      WHERE PersonId = $id ORDER BY ValidFromLocalDate DESC LIMIT 1)
                  AND ValidFromLocalDate < $date;
                """;
            reopen.Parameters.Add("$id", SqliteType.Text).Value = personId;
            reopen.Parameters.Add("$date", SqliteType.Text).Value = effectiveDate;
            await reopen.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await BridgeAuditWriter.WriteAsync(connection, tx, _time,
                operationCode: "PERSON_RESTORE", operationArabicName: "استرجاع فرد مؤرشف",
                actorUserId: userId, actorUsername: username, actorDisplayName: name,
                targetEntityType: "Person", targetEntityId: personId, targetDisplay: name,
                oldValueJson: new JsonObject { ["organizationalStatus"] = 4 }.ToJsonString(),
                newValueJson: new JsonObject { ["organizationalStatus"] = 1 }.ToJsonString(),
                description: $"استُرجع «{name}» إلى القوة الفعالة اعتباراً من {effectiveDate} (أرشفة عكسية تاريخية).",
                reason: ReadString(payload, "reason"), effectiveLocalDate: effectiveDate,
                correlationId: ct.CorrelationId(), ct).ConfigureAwait(false);

            return BridgeHelpers.Ok(new JsonObject { ["personId"] = personId, ["restored"] = true },
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

    private string Today() => DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd");

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
        try { return node.GetValue<long>(); } catch { return fallback; }
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
