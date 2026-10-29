using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Domain.Policies;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AlTamam.Application.Bridge;

/// <summary>
/// Phase 11 — UserAuditService audit portion: immutable evidence with visible overlays.
/// AuditEvents rows are never updated or deleted; GA/OA overlay versions live in
/// AuditLogPresentationEdits (append-only, versioned). Every query is parameterized and
/// every read projects the latest overlay on top of raw evidence so hidden/edited entries
/// remain fully recoverable (gate: immutable evidence survives visual edits).
/// </summary>
public sealed class AuditOverlayService
{
    private const int MaxPageSize = 200;
    private static readonly string[] ValidDatePattern = ["yyyy-MM-dd"];

    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _time;

    public AuditOverlayService(AlTamamDbContext database, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _db = database;
        _time = timeProvider ?? TimeProvider.System;
    }

    // ---------------------------------------------------------- QueryAuditLog (11.02)

    /// <summary>
    /// Filter/search audit history by actor, name, service number, date range, operation and target.
    /// All filters bind through parameters only; LIKE input is escaped. The projection applies the
    /// latest presentation overlay (corrected description / tombstone) while always returning the
    /// immutable raw fields alongside it.
    /// </summary>
    public async Task<BridgeResponse> QueryAuditLogAsync(
        JsonObject payload, CancellationToken ct, BridgePrincipal? principal = null)
    {
        string term = ReadString(payload, "term");
        string actor = ReadString(payload, "actor");
        string target = ReadString(payload, "target");
        string operation = ReadString(payload, "operation");
        string from = ReadString(payload, "fromDate");
        string to = ReadString(payload, "toDate");
        bool includeTombstoned = ReadBool(payload, "includeTombstoned", false);
        if (includeTombstoned && principal?.Role is not
            UserRole.GeneralAdministration)
            return BridgeHelpers.Error(BridgeErrorCodes.Unauthorized,
                "عرض القيود المخفية محصور بالإدارة العامة.");
        int page = Math.Max(ReadInt(payload, "page", 1), 1);
        int pageSize = Math.Clamp(ReadInt(payload, "pageSize", 50), 1, MaxPageSize);

        if (!string.IsNullOrWhiteSpace(from) && !IsValidDate(from))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "تاريخ البداية غير صالح (yyyy-MM-dd).");
        if (!string.IsNullOrWhiteSpace(to) && !IsValidDate(to))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "تاريخ النهاية غير صالح (yyyy-MM-dd).");

        var where = new List<string>();
        var args = new List<(string Name, object Value)>();
        if (!string.IsNullOrWhiteSpace(term))
        {
            // Search all matching audit entries BEFORE paging. Overlay edits
            // never rewrite immutable AuditEvents, but are searchable as displayed.
            where.Add("""
                (altamam_normalize_search(e.ActorUsernameSnapshot) LIKE $term ESCAPE '\'
                 OR altamam_normalize_search(e.ActorRosterFullNameSnapshot) LIKE $term ESCAPE '\'
                 OR altamam_normalize_search(e.TargetDisplaySnapshot) LIKE $term ESCAPE '\'
                 OR altamam_normalize_search(e.TargetEntityId) LIKE $term ESCAPE '\'
                 OR altamam_normalize_search(e.OperationCode) LIKE $term ESCAPE '\'
                 OR altamam_normalize_search(e.OperationArabicName) LIKE $term ESCAPE '\'
                 OR altamam_normalize_search(e.DefaultArabicDescription) LIKE $term ESCAPE '\'
                 OR altamam_normalize_search(e.OldValueJson) LIKE $term ESCAPE '\'
                 OR altamam_normalize_search(e.NewValueJson) LIKE $term ESCAPE '\'
                 OR EXISTS(SELECT 1 FROM AuditLogPresentationEdits o
                           WHERE o.AuditEventId = e.AuditEventId
                             AND o.OverlayVersion =
                                 (SELECT MAX(p.OverlayVersion) FROM AuditLogPresentationEdits p
                                  WHERE p.AuditEventId = e.AuditEventId)
                             AND altamam_normalize_search(o.CorrectedDescription) LIKE $term ESCAPE '\'))
                """);
            args.Add(("$term", "%" + EscapeLike(NormalizeTerm(term)) + "%"));
        }
        if (!string.IsNullOrWhiteSpace(actor))
        {
            where.Add("(altamam_normalize_search(e.ActorUsernameSnapshot) LIKE $actor ESCAPE '\\' OR altamam_normalize_search(e.ActorRosterFullNameSnapshot) LIKE $actor ESCAPE '\\')");
            args.Add(("$actor", "%" + EscapeLike(NormalizeTerm(actor)) + "%"));
        }
        if (!string.IsNullOrWhiteSpace(target))
        {
            where.Add("(altamam_normalize_search(e.TargetDisplaySnapshot) LIKE $target ESCAPE '\\' OR altamam_normalize_search(e.TargetEntityId) LIKE $target ESCAPE '\\' OR altamam_normalize_search(e.OldValueJson) LIKE $target ESCAPE '\\' OR altamam_normalize_search(e.NewValueJson) LIKE $target ESCAPE '\\')");
            args.Add(("$target", "%" + EscapeLike(NormalizeTerm(target)) + "%"));
        }
        if (!string.IsNullOrWhiteSpace(operation))
        {
            where.Add("e.OperationCode LIKE $op ESCAPE '\\'");
            args.Add(("$op", NormalizeTerm(operation) + "%"));
        }
        if (!string.IsNullOrWhiteSpace(from))
        {
            where.Add("date(e.ExecutedAtUtc) >= date($from)");
            args.Add(("$from", from));
        }
        if (!string.IsNullOrWhiteSpace(to))
        {
            where.Add("date(e.ExecutedAtUtc) <= date($to)");
            args.Add(("$to", to));
        }
        if (!includeTombstoned)
        {
            where.Add("COALESCE((SELECT o.IsTombstoned FROM AuditLogPresentationEdits o" +
                      " WHERE o.AuditEventId = e.AuditEventId ORDER BY o.OverlayVersion DESC LIMIT 1), 0) = 0");
        }

        string whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        using var connection = _db.CreateOpenConnection();
        // Match normalized Arabic names, diacritics and Arabic-Indic digits
        // in the stored snapshots WITHOUT ever rewriting immutable audit data.
        connection.CreateFunction<string, string>("altamam_normalize_search",
            value => ArabicTextNormalizer.NormalizeForSearch(value),
            isDeterministic: true);
        using var countCmd = connection.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM AuditEvents e {whereSql};";
        foreach (var (n, v) in args) countCmd.Parameters.Add(n, SqliteType.Text).Value = v;
        long total = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

        using var listCmd = connection.CreateCommand();
        listCmd.CommandText = $"""
            SELECT e.AuditEventId, e.OperationCode, e.OperationArabicName, e.ActorUsernameSnapshot,
                   e.ActorRosterFullNameSnapshot, e.TargetEntityType, e.TargetEntityId, e.TargetDisplaySnapshot,
                   e.DefaultArabicDescription, e.Reason, e.EffectiveLocalDate, e.ExecutedAtUtc, e.EventHash,
                   COALESCE((SELECT o.CorrectedDescription FROM AuditLogPresentationEdits o
                             WHERE o.AuditEventId = e.AuditEventId ORDER BY o.OverlayVersion DESC LIMIT 1), ''),
                   COALESCE((SELECT o.IsTombstoned FROM AuditLogPresentationEdits o
                             WHERE o.AuditEventId = e.AuditEventId ORDER BY o.OverlayVersion DESC LIMIT 1), 0),
                   COALESCE((SELECT o.OverlayVersion FROM AuditLogPresentationEdits o
                             WHERE o.AuditEventId = e.AuditEventId ORDER BY o.OverlayVersion DESC LIMIT 1), 0),
                   e.ActorPersonId, e.OldValueJson, e.NewValueJson,
                   CASE WHEN e.ActorPersonId IS NULL AND EXISTS(
                     SELECT 1 FROM AuditEvents founder
                     WHERE founder.ActorUserId=e.ActorUserId
                       AND founder.OperationCode='FOUNDING_ADMIN_CREATED')
                     THEN 1 ELSE 0 END
            FROM AuditEvents e
            {whereSql}
            ORDER BY e.ExecutedAtUtc DESC, e.AuditEventId DESC
            LIMIT $limit OFFSET $offset;
            """;
        foreach (var (n, v) in args) listCmd.Parameters.Add(n, SqliteType.Text).Value = v;
        listCmd.Parameters.Add("$limit", SqliteType.Integer).Value = pageSize;
        listCmd.Parameters.Add("$offset", SqliteType.Integer).Value = (page - 1) * pageSize;

        var items = new JsonArray();
        using var reader = await listCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string rawDescription = reader.GetString(8);
            string corrected = reader.IsDBNull(13) ? "" : reader.GetString(13);
            string usernameSnapshot=reader.GetString(3);
            string? rosterSnapshot=reader.IsDBNull(4)?null:reader.GetString(4);
            bool founder=reader.GetInt32(19)==1;
            string founderName=rosterSnapshot?.StartsWith("الإدارة العامة",StringComparison.Ordinal)==true
                ? rosterSnapshot["الإدارة العامة".Length..].Trim(' ','-')
                : rosterSnapshot??"";
            string display=founder
                ? "الإدارة العامة - "+usernameSnapshot+
                    (founderName.Length>0?" ("+founderName+")":"")
                : usernameSnapshot+(string.IsNullOrWhiteSpace(rosterSnapshot)?"":" - "+rosterSnapshot);
            items.Add(new JsonObject
            {
                ["sequence"]=(long)(page-1)*pageSize+items.Count+1,
                ["actorDisplay"]=display,
                ["outcome"]=reader.GetString(1).EndsWith("FAILED",StringComparison.OrdinalIgnoreCase)
                    ? "فشل" : "تم الاعتماد",
                ["oldValue"]=reader.IsDBNull(17)?null:JsonNode.Parse(reader.GetString(17)),
                ["newValue"]=reader.IsDBNull(18)?null:JsonNode.Parse(reader.GetString(18)),
                ["auditEventId"] = reader.GetString(0),
                ["operationCode"] = reader.GetString(1),
                ["operationArabicName"] = reader.GetString(2),
                ["actorUsername"] = reader.GetString(3),
                ["actorRosterFullName"] = reader.IsDBNull(4) ? null : reader.GetString(4),
                ["targetEntityType"] = reader.GetString(5),
                ["targetEntityId"] = reader.GetString(6),
                ["targetDisplay"] = reader.GetString(7),
                ["description"] = corrected.Length > 0 ? corrected : rawDescription,
                ["rawDescription"] = rawDescription,
                ["reason"] = reader.IsDBNull(9) ? null : reader.GetString(9),
                ["effectiveLocalDate"] = reader.GetString(10),
                ["executedAtUtc"] = reader.GetString(11),
                ["eventHash"] = reader.GetString(12),
                ["isTombstoned"] = reader.GetInt32(14) == 1,
                ["overlayVersion"] = reader.GetInt32(15)
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

    // ------------------------------------------------------ GetAuditEntryDetails (11.04)

    /// <summary>
    /// Full immutable entry plus its complete overlay chain and hash-chain validation result.
    /// Supervisor may view but never modify (enforced by manifest roles at dispatch).
    /// </summary>
    public async Task<BridgeResponse> GetAuditEntryDetailsAsync(JsonObject payload, CancellationToken ct)
    {
        string auditEventId = ReadString(payload, "auditEventId");
        if (string.IsNullOrWhiteSpace(auditEventId))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "معرّف القيد مطلوب.");

        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT OperationCode, OperationArabicName, ActorUserId, ActorUsernameSnapshot,
                   ActorRosterFullNameSnapshot, TargetEntityType, TargetEntityId, TargetDisplaySnapshot,
                   OldValueJson, NewValueJson, DefaultArabicDescription, Reason, EffectiveLocalDate,
                   ExecutedAtUtc, CorrelationId, EventHash, PriorEventHash,
                   CASE WHEN ActorPersonId IS NULL AND EXISTS(
                       SELECT 1 FROM AuditEvents founder
                       WHERE founder.ActorUserId=AuditEvents.ActorUserId
                         AND founder.OperationCode='FOUNDING_ADMIN_CREATED')
                     THEN 1 ELSE 0 END
            FROM AuditEvents WHERE AuditEventId = $id;
            """;
        cmd.Parameters.AddWithValue("$id", auditEventId);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "القيد غير موجود.");

        var entry = new JsonObject
        {
            ["auditEventId"] = auditEventId,
            ["operationCode"] = reader.GetString(0),
            ["operationArabicName"] = reader.GetString(1),
            ["actorUserId"] = reader.GetString(2),
            ["actorUsername"] = reader.GetString(3),
            ["actorRosterFullName"] = reader.IsDBNull(4) ? null : reader.GetString(4),
            ["actorDisplay"] = reader.GetInt32(17)==1
                ? "الإدارة العامة - "+reader.GetString(3)+
                    (reader.IsDBNull(4) || reader.GetString(4).Length==0 ||
                     reader.GetString(4)=="الإدارة العامة"?""
                        :" ("+(reader.GetString(4).StartsWith("الإدارة العامة",StringComparison.Ordinal)
                           ? reader.GetString(4)["الإدارة العامة".Length..].Trim(' ','-')
                           : reader.GetString(4))+")")
                : reader.GetString(3)+(reader.IsDBNull(4)?"":" - "+reader.GetString(4)),
            ["targetEntityType"] = reader.GetString(5),
            ["targetEntityId"] = reader.GetString(6),
            ["targetDisplay"] = reader.GetString(7),
            ["oldValue"] = reader.IsDBNull(8) ? null : JsonNode.Parse(reader.GetString(8)),
            ["newValue"] = reader.IsDBNull(9) ? null : JsonNode.Parse(reader.GetString(9)),
            ["rawDescription"] = reader.GetString(10),
            ["reason"] = reader.IsDBNull(11) ? null : reader.GetString(11),
            ["effectiveLocalDate"] = reader.GetString(12),
            ["executedAtUtc"] = reader.GetString(13),
            ["correlationId"] = reader.GetString(14),
            ["eventHash"] = reader.GetString(15),
            ["priorEventHash"] = reader.GetString(16)
        };
        reader.Close();

        var overlays = new JsonArray();
        using var ovCmd = connection.CreateCommand();
        ovCmd.CommandText = """
            SELECT OverlayVersion, IsTombstoned, CorrectedDescription, Reason, EditedByUserId,
                   EditedByDisplaySnapshot, EditedAtUtc
            FROM AuditLogPresentationEdits WHERE AuditEventId = $id ORDER BY OverlayVersion ASC;
            """;
        ovCmd.Parameters.AddWithValue("$id", auditEventId);
        using var ovReader = await ovCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await ovReader.ReadAsync(ct).ConfigureAwait(false))
        {
            overlays.Add(new JsonObject
            {
                ["overlayVersion"] = ovReader.GetInt32(0),
                ["isTombstoned"] = ovReader.GetInt32(1) == 1,
                ["correctedDescription"] = ovReader.IsDBNull(2) ? null : ovReader.GetString(2),
                ["reason"] = ovReader.GetString(3),
                ["editedByUserId"] = ovReader.GetString(4),
                ["editedByDisplay"] = ovReader.GetString(5),
                ["editedAtUtc"] = ovReader.GetString(6)
            });
        }
        ovReader.Close();

        entry["overlays"] = overlays;
        entry["chainValid"] = ValidateChain(connection, auditEventId, entry["eventHash"]!.GetValue<string>(),
            entry["priorEventHash"]!.GetValue<string>(),
            entry["operationCode"]!.GetValue<string>(), entry["operationArabicName"]!.GetValue<string>(),
            entry["actorUserId"]!.GetValue<string>(), entry["actorUsername"]!.GetValue<string>(),
            entry["actorRosterFullName"]?.GetValue<string>(),
            entry["targetEntityType"]!.GetValue<string>(), entry["targetEntityId"]!.GetValue<string>(),
            entry["targetDisplay"]!.GetValue<string>(),
            entry["oldValue"]?.ToJsonString(), entry["newValue"]?.ToJsonString(),
            entry["rawDescription"]!.GetValue<string>(), entry["reason"]?.GetValue<string>(),
            entry["effectiveLocalDate"]!.GetValue<string>(), entry["executedAtUtc"]!.GetValue<string>(),
            entry["correlationId"]!.GetValue<string>());

        return BridgeHelpers.Ok(entry);
    }

    // ------------------------------------------- UpdateAuditPresentation (11.03 edit)

    /// <summary>
    /// Visible-description edit as a NEW overlay version. Raw AuditEvents row is untouched;
    /// the overlay itself is recorded inside the same transaction (who changed what and when).
    /// Requires reason and expected current overlay version (stale-overlay rejection → ConflictStale).
    /// </summary>
    public Task<BridgeResponse> UpdateAuditPresentationAsync(
        JsonObject payload, BridgePrincipal principal, CancellationToken ct) =>
        WriteOverlayAsync(payload, principal, tombstone: false, ct);

    // ----------------------------------------- DeleteAuditPresentation (11.03 tombstone)

    /// <summary>Visible delete = tombstone overlay. Evidence remains in AuditEvents forever.</summary>
    public Task<BridgeResponse> DeleteAuditPresentationAsync(
        JsonObject payload, BridgePrincipal principal, CancellationToken ct) =>
        WriteOverlayAsync(payload, principal, tombstone: true, ct);

    private async Task<BridgeResponse> WriteOverlayAsync(
        JsonObject payload, BridgePrincipal principal, bool tombstone, CancellationToken ct)
    {
        // Defense in depth: the Application service owns authorization. A caller
        // bypassing the WebView dispatcher must not edit visible evidence.
        if (principal.Role != UserRole.GeneralAdministration)
            return BridgeHelpers.Error(BridgeErrorCodes.Unauthorized,
                "تعديل أو إخفاء عرض قيود العمليات محصور بالإدارة العامة فقط.");
        string auditEventId = ReadString(payload, "auditEventId");
        string reason = ReadString(payload, "reason");
        string corrected = ReadString(payload, "correctedDescription");
        long expectedVersion = ReadInt(payload, "expectedOverlayVersion", 0);

        if (string.IsNullOrWhiteSpace(auditEventId))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "معرّف القيد مطلوب.");
        if (string.IsNullOrWhiteSpace(reason))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "السبب إلزامي لتعديل أو إخفاء قيد السجل.");
        if (!tombstone && string.IsNullOrWhiteSpace(corrected))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "الوصف البديل مطلوب للتعديل الظاهر.");

        string editedAtUtc = _time.GetUtcNow().ToString("O");

        return await _db.ExecuteInWriteTransactionAsync(async (connection, transaction) =>
        {
            // An overlay, its immutable meta-audit and its success receipt are
            // ONE atomic operation. A retry of the same exact signed-in user's
            // intent must not append another overlay or duplicate audit row.
            var (replay, receipt) = await MutationIdempotency.StartAsync(
                connection, transaction,
                tombstone ? "DeleteAuditPresentation" : "UpdateAuditPresentation",
                principal.UserId, payload, ct).ConfigureAwait(false);
            if (replay is not null) return replay;
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "SELECT 1 FROM AuditEvents WHERE AuditEventId = $id;";
            check.Parameters.AddWithValue("$id", auditEventId);
            if (await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
                return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "القيد غير موجود.");

            using var verCmd = connection.CreateCommand();
            verCmd.Transaction = transaction;
            verCmd.CommandText = "SELECT COALESCE(MAX(OverlayVersion), 0) FROM AuditLogPresentationEdits WHERE AuditEventId = $id;";
            verCmd.Parameters.AddWithValue("$id", auditEventId);
            long currentVersion = Convert.ToInt64(await verCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (currentVersion != expectedVersion)
                return BridgeHelpers.Error(BridgeErrorCodes.ConflictStale,
                    $"تم تعديل القيد من مستخدم آخر (الإصدار الحالي {currentVersion}). حدّث الشاشة وأعد المحاولة.");

            // Meta-audit: the overlay action itself is immutable evidence (atomic in same tx).
            await BridgeAuditWriter.WriteAsync(connection, transaction, _time,
                tombstone ? "DeleteAuditPresentation" : "UpdateAuditPresentation",
                tombstone ? "إخفاء قيد في سجل العمليات" : "تعديل الوصف الظاهر لقيد في سجل العمليات",
                principal.UserId, principal.Username, actorDisplayName: null,
                "AuditEvent", auditEventId, auditEventId,
                BridgeAuditWriter.Json(new JsonObject { ["overlayVersion"] = currentVersion }),
                BridgeAuditWriter.Json(new JsonObject { ["overlayVersion"] = currentVersion + 1,
                                                        ["tombstoned"] = tombstone, ["correctedDescription"] = corrected }),
                tombstone
                    ? $"أُخفي القيد {auditEventId} ظاهرياً بواسطة {principal.Username}."
                    : $"عُدّل الوصف الظاهر للقيد {auditEventId} بواسطة {principal.Username}.",
                reason, DateOnly.FromDateTime(_time.GetUtcNow().DateTime).ToString("yyyy-MM-dd"),
                ct.CorrelationId(), ct).ConfigureAwait(false);

            // Append-only overlay version; raw evidence is never mutated.
            using var ins = connection.CreateCommand();
            ins.Transaction = transaction;
            ins.CommandText = """
                INSERT INTO AuditLogPresentationEdits
                    (AuditEventId, OverlayVersion, IsTombstoned, CorrectedDescription, Reason,
                     EditedByUserId, EditedByDisplaySnapshot, EditedAtUtc)
                VALUES ($id, $version, $tomb, $corrected, $reason, $by, $byDisplay, $at);
                """;
            ins.Parameters.AddWithValue("$id", auditEventId);
            ins.Parameters.AddWithValue("$version", currentVersion + 1);
            ins.Parameters.AddWithValue("$tomb", tombstone ? 1 : 0);
            ins.Parameters.AddWithValue("$corrected", tombstone ? DBNull.Value : corrected);
            ins.Parameters.AddWithValue("$reason", reason);
            ins.Parameters.AddWithValue("$by", principal.UserId);
            // The actor's immutable event-time identity was just captured by
            // BridgeAuditWriter in the same transaction. Never use a later roster lookup.
            string byDisplay=principal.Username;
            using(var identity=connection.CreateCommand())
            {
                identity.Transaction=transaction;
                identity.CommandText="""
                    SELECT ActorUsernameSnapshot,ActorRosterFullNameSnapshot
                    FROM AuditEvents WHERE OperationCode=$code AND TargetEntityId=$id
                    ORDER BY rowid DESC LIMIT 1;
                    """;
                identity.Parameters.AddWithValue("$code",
                    tombstone?"DeleteAuditPresentation":"UpdateAuditPresentation");
                identity.Parameters.AddWithValue("$id",auditEventId);
                using var actor=await identity.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if(await actor.ReadAsync(ct).ConfigureAwait(false))
                {
                    string name=actor.GetString(0);
                    string? historical=actor.IsDBNull(1)?null:actor.GetString(1);
                    byDisplay=historical?.StartsWith("الإدارة العامة",StringComparison.Ordinal)==true
                        ? "الإدارة العامة - "+name+
                            (historical.Length>"الإدارة العامة".Length
                                ?" ("+historical["الإدارة العامة".Length..].Trim(' ','-')+")":"")
                        : name+(string.IsNullOrWhiteSpace(historical)?"":" - "+historical);
                }
            }
            ins.Parameters.AddWithValue("$byDisplay", byDisplay);
            ins.Parameters.AddWithValue("$at", editedAtUtc);
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            var result = new JsonObject
            {
                ["auditEventId"] = auditEventId,
                ["overlayVersion"] = currentVersion + 1,
                ["isTombstoned"] = tombstone,
                ["rawEvidenceUntouched"] = true
            };
            await MutationIdempotency.StoreAsync(connection, transaction,
                receipt, result, _time, ct).ConfigureAwait(false);
            return BridgeHelpers.Ok(result);
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------- QueryAuditPresentationChanges (11.04 overlay history)

    /// <summary>All overlay versions across all entries — the «القيود المعدلة والمحذوفة» screen feed.</summary>
    public async Task<BridgeResponse> QueryAuditPresentationChangesAsync(JsonObject payload, CancellationToken ct)
    {
        string auditEventId = ReadString(payload, "auditEventId");
        int page = Math.Max(ReadInt(payload, "page", 1), 1);
        int pageSize = Math.Clamp(ReadInt(payload, "pageSize", 50), 1, MaxPageSize);

        string filter = string.IsNullOrWhiteSpace(auditEventId) ? "" : "WHERE o.AuditEventId = $id";
        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        if (!string.IsNullOrWhiteSpace(auditEventId)) cmd.Parameters.AddWithValue("$id", auditEventId);
        cmd.CommandText = $"""
            SELECT o.AuditEventId, o.OverlayVersion, o.IsTombstoned, o.CorrectedDescription, o.Reason,
                   o.EditedByUserId, o.EditedByDisplaySnapshot, o.EditedAtUtc,
                   e.OperationCode, e.DefaultArabicDescription
            FROM AuditLogPresentationEdits o
            JOIN AuditEvents e ON e.AuditEventId = o.AuditEventId
            {filter}
            ORDER BY o.EditedAtUtc DESC, o.OverlayVersion DESC
            LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.Add("$limit", SqliteType.Integer).Value = pageSize;
        cmd.Parameters.Add("$offset", SqliteType.Integer).Value = (page - 1) * pageSize;

        var items = new JsonArray();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new JsonObject
            {
                ["auditEventId"] = reader.GetString(0),
                ["overlayVersion"] = reader.GetInt32(1),
                ["isTombstoned"] = reader.GetInt32(2) == 1,
                ["correctedDescription"] = reader.IsDBNull(3) ? null : reader.GetString(3),
                ["reason"] = reader.GetString(4),
                ["editedByUserId"] = reader.GetString(5),
                ["editedByDisplay"] = reader.GetString(6),
                ["editedAtUtc"] = reader.GetString(7),
                ["operationCode"] = reader.GetString(8),
                ["rawDescription"] = reader.GetString(9)
            });
        }
        return BridgeHelpers.Ok(new JsonObject { ["items"] = items, ["count"] = items.Count, ["page"] = page });
    }

    // ---------------------------------- ClosedDayExceptionalCorrection (11.05) — audit portion

    /// <summary>
    /// Two-step emergency correction gate for a closed DAY period. Step 1 (preview): validates the
    /// day is actually closed and issues nothing mutable — returns a signed challenge binding the
    /// exact day + actor + reason hash. Step 2 (execute): re-validates closure and permission and
    /// writes ONLY the structured audit evidence row; the attendance mutation itself belongs to the
    /// AttendanceService commit path which consumes the returned challenge token. This keeps the
    /// correction strictly General/Ordinary Administration-gated, reason-bound and atomically audited.
    /// </summary>
    public async Task<BridgeResponse> RequestClosedDayCorrectionAsync(
        JsonObject payload, BridgePrincipal principal, CancellationToken ct)
    {
        if (principal.Role is not (UserRole.GeneralAdministration or UserRole.OrdinaryAdministration))
            return BridgeHelpers.Error(BridgeErrorCodes.Unauthorized,
                "التصحيح الاستثنائي لليوم المغلق متاح للإدارة العامة والإدارة العادية فقط.");
        string day = ReadString(payload, "businessDate");
        string reason = ReadString(payload, "reason");
        if (!IsValidDate(day))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError, "تاريخ اليوم غير صالح (yyyy-MM-dd).");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "السبب إلزامي ويجب ألا يقل عن 10 أحرف.");

        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT IsClosed, ClosedByActor FROM PeriodClosures WHERE PeriodKey = $key AND PeriodType = 'DAY';";
        cmd.Parameters.AddWithValue("$key", day);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        bool closed = reader.Read() && !reader.IsDBNull(0) && reader.GetInt32(0) == 1;
        reader.Close();
        if (!closed)
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "هذا اليوم غير مغلق؛ لا حاجة للتصحيح الاستثنائي — استخدم المسار الاعتيادي.");

        string challengeId = "CHG-" + Guid.NewGuid().ToString("N");
        string binding = string.Join('\n', challengeId, day, principal.UserId, reason.Trim());
        string signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding))).ToLowerInvariant();

        return BridgeHelpers.Ok(new JsonObject
        {
            ["challengeId"] = challengeId,
            ["signature"] = signature,
            ["businessDate"] = day,
            ["requiresExplicitConfirmation"] = true,
            ["message"] = "أكّد التصحيح الاستثنائي؛ سيُسجَّل إجراء طارئ باسمك مع السبب في السجل غير القابل للتعديل."
        });
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsValidDate(string value) =>
        DateOnly.TryParseExact(value, ValidDatePattern, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);

    private static string NormalizeTerm(string term) =>
        ArabicTextNormalizer.NormalizeForSearch(term ?? "");

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string ReadString(JsonObject payload, string key)
    {
        try { return payload[key]?.GetValue<string>() ?? ""; } catch { return ""; }
    }

    private static int ReadInt(JsonObject payload, string key, int fallback)
    {
        try
        {
            var v = payload[key];
            return v is null ? fallback : v.GetValue<int>();
        }
        catch { return fallback; }
    }

    private static long ReadLong(JsonObject payload, string key, long fallback)
    {
        try
        {
            var v = payload[key];
            return v is null ? fallback : v.GetValue<long>();
        }
        catch { return fallback; }
    }

    private static bool ReadBool(JsonObject payload, string key, bool fallback)
    {
        try
        {
            var v = payload[key];
            return v is null ? fallback : v.GetValue<bool>();
        }
        catch { return fallback; }
    }

    /// <summary>
    /// Recomputes the canonical SHA-256 envelope for one stored event and compares it to EventHash.
    /// A mismatch means the physical DB file was tampered with outside the application.
    /// </summary>
    private static bool ValidateChain(
        SqliteConnection connection, string auditEventId, string eventHash, string priorEventHash,
        string operationCode, string operationArabicName, string actorUserId, string actorUsername,
        string? actorDisplayName, string targetEntityType, string targetEntityId, string targetDisplay,
        string? oldValueJson, string? newValueJson, string description, string? reason,
        string effectiveLocalDate, string executedAtUtc, string correlationId)
    {
        string body = string.Join('\n',
            operationCode, operationArabicName, actorUserId, actorUsername, actorDisplayName ?? "",
            targetEntityType, targetEntityId, targetDisplay, oldValueJson ?? "", newValueJson ?? "",
            description, reason ?? "", effectiveLocalDate, executedAtUtc, correlationId, priorEventHash);
        string recomputed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        return string.Equals(recomputed, eventHash, StringComparison.Ordinal);
    }
}
