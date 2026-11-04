using System.Globalization;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;

namespace AlTamam.Application.Scheduling;

/// <summary>
/// Opt-in local automatic monthly workbooks. Each file is a current projection
/// of the SAME effective-dated monthly SQLCipher roster as the manual exporter.
/// Missed scheduled days are caught up by regenerating their month(s), never by
/// inventing a historical snapshot for a day on which Windows was powered off.
/// The manual, uniquely named Excel exports are not overwritten.
/// </summary>
public sealed class AutomaticMonthlyExcelService
{
    private readonly AlTamamDbContext _db;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public AutomaticMonthlyExcelService(AlTamamDbContext db, TimeProvider? clock = null)
    { _db = db; _clock = clock ?? TimeProvider.System; }

    public sealed record Options(bool Enabled, string ExportTime, string Directory,
        string LastSuccessDate, string LastFailure);

    private static readonly string[] Keys =
    [
        "AutoMonthlyExcelEnabled", "AutoMonthlyExcelTime", "AutoMonthlyExcelDirectory",
        "AutoMonthlyExcelLastSuccess", "AutoMonthlyExcelLastFailure"
    ];

    public async Task<Options> ReadAsync(CancellationToken ct = default)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        using var c = _db.CreateOpenConnection();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT SettingKey,SettingValue FROM AppSettings WHERE SettingKey IN (" +
            string.Join(",", Keys.Select((_, i) => "$k" + i)) + ");";
        for (int i = 0; i < Keys.Length; i++) q.Parameters.AddWithValue("$k" + i, Keys[i]);
        using var reader = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            data[reader.GetString(0)] = reader.GetString(1);
        string Get(string key, string fallback = "") => data.GetValueOrDefault(key, fallback);
        return new Options(Get(Keys[0]) == "true", Get(Keys[1], "21:00"), Get(Keys[2]),
            Get(Keys[3]), Get(Keys[4]));
    }

    public static JsonObject AsJson(Options state) => new()
    {
        ["enabled"] = state.Enabled, ["exportTime"] = state.ExportTime,
        ["directory"] = state.Directory, ["lastSuccessDate"] = state.LastSuccessDate,
        ["lastFailure"] = state.LastFailure
    };

    public async Task<BridgeResponse> ConfigureAsync(JsonObject? payload,
        BridgePrincipal actor, CancellationToken ct)
    {
        if (actor.Role is not (UserRole.GeneralAdministration or UserRole.OrdinaryAdministration))
            return BridgeHelpers.Error(BridgeErrorCodes.Unauthorized,
                "إعداد التصدير التلقائي متاح للإدارة فقط.");
        bool enabled;
        string exportTime, directory;
        try
        {
            enabled = payload?["enabled"]?.GetValue<bool>()
                ?? throw new ArgumentException("Missing enabled");
            exportTime = payload?["exportTime"]?.GetValue<string>() ?? "";
            directory = payload?["directory"]?.GetValue<string>()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(payload?["operationId"]?.GetValue<string>()))
                throw new ArgumentException("Missing operationId");
        }
        catch
        {
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "حدد حالة التفعيل والوقت والمجلد قبل الحفظ.");
        }
        if (!TimeOnly.TryParseExact(exportTime, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            return BridgeHelpers.Error(BridgeErrorCodes.ValidationError,
                "وقت التصدير يجب أن يكون 24 ساعة بصيغة HH:mm.");
        if (directory.Length > 0 && !OutputDirectorySettings.IsUsableDirectory(directory, _db.DatabasePath))
            return BridgeHelpers.Error(BridgeErrorCodes.ScheduleUnavailable,
                "مجلد تصدير التمام غير متاح أو غير قابل للكتابة، اختر مجلداً محلياً صالحاً.");
        if (enabled && directory.Length == 0)
            return BridgeHelpers.Error(BridgeErrorCodes.ScheduleUnavailable,
                "اختر مجلد التصدير من Windows قبل تفعيل التصدير التلقائي.");

        return await _db.ExecuteInWriteTransactionAsync(async (c, tx) =>
        {
            var (replay, receipt) = await MutationIdempotency.StartAsync(c, tx,
                "ConfigureAutoMonthlyExcel", actor.UserId, payload, ct).ConfigureAwait(false);
            if (replay is not null) return replay;
            // Read the previous values INSIDE the write transaction to preserve
            // schedule independence and avoid losing missed-day catch-up.
            var old = new Dictionary<string,string>(StringComparer.Ordinal);
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT SettingKey,SettingValue FROM AppSettings WHERE SettingKey IN (" +
                    string.Join(",", Keys.Select((_,i) => "$k" + i)) + ");";
                for (int i = 0; i < Keys.Length; i++) q.Parameters.AddWithValue("$k" + i, Keys[i]);
                using var reader = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    old[reader.GetString(0)] = reader.GetString(1);
            }
            bool previouslyEnabled = old.GetValueOrDefault(Keys[0]) == "true";
            string previousDirectory = old.GetValueOrDefault(Keys[2], "");
            bool reset = enabled && (!previouslyEnabled ||
                !string.Equals(directory, previousDirectory, StringComparison.OrdinalIgnoreCase));
            string lastSuccess = reset ? "" : old.GetValueOrDefault(Keys[3], "");
            string lastFailure = reset ? "" : old.GetValueOrDefault(Keys[4], "");
            string[] values = [enabled ? "true" : "false", exportTime, directory,
                lastSuccess, lastFailure];
            for (int i = 0; i < Keys.Length; i++)
            {
                using var update = c.CreateCommand();
                update.Transaction = tx;
                update.CommandText = """
                    INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                    VALUES($key,$value,$at)
                    ON CONFLICT(SettingKey) DO UPDATE SET
                      SettingValue=excluded.SettingValue,UpdatedAtUtc=excluded.UpdatedAtUtc;
                    """;
                update.Parameters.AddWithValue("$key", Keys[i]);
                update.Parameters.AddWithValue("$value", values[i]);
                update.Parameters.AddWithValue("$at", _clock.GetUtcNow().ToString("O"));
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            var current = new Options(enabled, exportTime, directory, lastSuccess, lastFailure);
            await BridgeAuditWriter.WriteAsync(c, tx, _clock,
                "AUTO_MONTHLY_EXCEL_CONFIGURED", "إعداد التصدير التلقائي للتمام اليومي",
                actor.UserId, actor.Username, actor.Username, "AppSettings", Keys[0],
                "التصدير التلقائي للتمام اليومي",
                new JsonObject{["enabled"]=previouslyEnabled,
                    ["time"]=old.GetValueOrDefault(Keys[1],"21:00"),
                    ["directory"]=previousDirectory}.ToJsonString(),
                new JsonObject{["enabled"]=enabled,["time"]=exportTime,
                    ["directory"]=directory}.ToJsonString(),
                "جدولة مستقلة عن النسخ الاحتياطي والتحضير والقفل.",
                null, _clock.GetLocalNow().ToString("yyyy-MM-dd"),
                Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
            var result = AsJson(current);
            await MutationIdempotency.StoreAsync(c, tx, receipt, result, _clock, ct)
                .ConfigureAwait(false);
            return BridgeHelpers.Ok(result);
        }, ct).ConfigureAwait(false);
    }

    private static string MonthLabel(int year, int month) =>
        new DateTime(year, month, 1).ToString("MMMM yyyy", new CultureInfo("ar-YE"));

    private sealed record Plan(string SiteId, string FolderName, string FileName);

    private async Task GenerateMonthAsync(string root, int year, int month, CancellationToken ct)
    {
        string label = MonthLabel(year, month);
        string monthFolder = Path.Combine(root, "تمام شهر " + label);
        string staging = Path.Combine(root, ".altamam-auto-month-" + Guid.NewGuid().ToString("N"));
        var plans = new List<Plan>
        {
            new("", "التمام الكلي للقوة",
                "التمام الكلي للقوة لشهر " + label + ".xlsx")
        };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "التمام الكلي للقوة" };
        using (var c = _db.CreateOpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT SiteId,VisibleName FROM Sites ORDER BY VisibleName,SiteId;";
            using var reader = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string id = reader.GetString(0);
                string name = ExcelOutputNames.Caption(reader.GetString(1), 40);
                string folder = name;
                if (!names.Add(folder))
                {
                    folder = name + " " + ExcelOutputNames.Caption(id, 16);
                    if (!names.Add(folder))
                        throw new InvalidOperationException("تعذر إنشاء أسماء مجلدات مواقع مستقلة.");
                }
                plans.Add(new Plan(id, folder, name + " لشهر " + label + ".xlsx"));
            }
        }
        try
        {
            Directory.CreateDirectory(staging);
            // All XLSX files are fully constructed and validated BEFORE any
            // customer workbook is replaced. No accidental .part files remain.
            var staged = new List<(string Source, string Destination)>();
            foreach (var plan in plans)
            {
                ct.ThrowIfCancellationRequested();
                var request = new JsonObject
                {
                    ["year"]=year, ["month"]=month, ["siteId"]=plan.SiteId,
                    ["directory"]=staging, ["fileName"]=plan.FileName,
                    ["consolidatedOnly"]=true
                };
                var result = await new MonthlyExcelExportService(_db)
                    .ExportScheduledSingleAsync(request, ct).ConfigureAwait(false);
                if (!result.Success)
                    throw new IOException("فشل تصدير " + plan.FolderName + ": " +
                        result.UserMessage);
                string source = ((JsonObject)result.Data!)["filePath"]!.GetValue<string>();
                staged.Add((source, Path.Combine(monthFolder, plan.FolderName, plan.FileName)));
            }
            // Restrict replacement to this feature's own managed monthly folder.
            // Refuse to overwrite an unrelated user's coincidentally named folder.
            string marker = Path.Combine(monthFolder, ".altamam-auto-monthly-managed");
            if (Directory.Exists(monthFolder) && !File.Exists(marker))
                throw new IOException("مجلد شهر موجود دون علامة إدارة التصدير التلقائي؛ لم تُستبدل ملفاته.");
            Directory.CreateDirectory(monthFolder);
            if (!File.Exists(marker))
                File.WriteAllText(marker, "AlTamam automatic monthly Excel; generated folder.");
            foreach (var (source, destination) in staged)
            {
                ct.ThrowIfCancellationRequested();
                string destinationFolder = Path.GetDirectoryName(destination)!;
                if (Directory.Exists(destinationFolder) &&
                    (new DirectoryInfo(destinationFolder).Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("مجلد موقع مرتبط بمسار آخر؛ لن تتم الكتابة خارجه.");
                Directory.CreateDirectory(destinationFolder);
                // Same-volume atomic rename, never truncate a previously valid XLSX.
                File.Move(source, destination, overwrite: true);
            }
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (IOException) { /* next unique staging directory cannot collide */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Rebuild each month with a missing due day exactly once. Because each
    /// month's output is a single cumulative workbook, missed daily executions
    /// are collapsed to ONE up-to-date workbook per affected month.
    /// </summary>
    public async Task<bool> RunIfDueAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = await ReadAsync(ct).ConfigureAwait(false);
            if (!settings.Enabled) return false;
            if (!TimeOnly.TryParseExact(settings.ExportTime, "HH:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly scheduled))
                throw new InvalidOperationException("وقت التصدير التلقائي غير صالح.");
            if (!OutputDirectorySettings.IsUsableDirectory(settings.Directory, _db.DatabasePath))
                throw new IOException("مجلد التصدير التلقائي غير متاح؛ لم تُسجل عملية ناجحة.");
            var now = _clock.GetLocalNow();
            DateOnly today = DateOnly.FromDateTime(now.DateTime);
            bool first = string.IsNullOrEmpty(settings.LastSuccessDate);
            DateOnly through = TimeOnly.FromDateTime(now.DateTime) >= scheduled
                ? today : today.AddDays(-1);
            if (first) through = today; // provision the actual monthly files once
            if (!first && (!DateOnly.TryParseExact(settings.LastSuccessDate,
                    "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out DateOnly previous) || previous >= through))
                return false;
            DateOnly start = first ? today :
                DateOnly.ParseExact(settings.LastSuccessDate, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                    .AddDays(1);
            DateOnly monthCursor = new(start.Year, start.Month, 1);
            DateOnly lastMonth = new(through.Year, through.Month, 1);
            // Bounded to 20 calendar years to prevent unbounded filesystem work
            // after clock corruption; valid missed days remain retryable.
            int months = 0;
            while (monthCursor <= lastMonth)
            {
                if (++months > 240)
                    throw new InvalidOperationException("الفترة المتأخرة تتجاوز حد المراجعة؛ راجع ساعة الجهاز.");
                await GenerateMonthAsync(settings.Directory, monthCursor.Year,
                    monthCursor.Month, ct).ConfigureAwait(false);
                monthCursor = monthCursor.AddMonths(1);
            }
            string date = through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await _db.ExecuteInWriteTransactionAsync(async(c,tx) =>
            {
                using var q = c.CreateCommand(); q.Transaction = tx;
                q.CommandText = """
                    INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                    VALUES('AutoMonthlyExcelLastSuccess',$date,$at)
                    ON CONFLICT(SettingKey) DO UPDATE SET
                      SettingValue=excluded.SettingValue,UpdatedAtUtc=excluded.UpdatedAtUtc;
                    INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                    VALUES('AutoMonthlyExcelLastFailure','',$at)
                    ON CONFLICT(SettingKey) DO UPDATE SET
                      SettingValue=excluded.SettingValue,UpdatedAtUtc=excluded.UpdatedAtUtc;
                    """;
                q.Parameters.AddWithValue("$date", date);
                q.Parameters.AddWithValue("$at", _clock.GetUtcNow().ToString("O"));
                await q.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return 0;
            }, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task RecordFailureAsync(string message, CancellationToken ct = default)
    {
        string detail = message.Length > 350 ? message[..350] : message;
        await _db.ExecuteInWriteTransactionAsync(async(c,tx) =>
        {
            using var q = c.CreateCommand(); q.Transaction=tx;
            q.CommandText = """
                INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
                VALUES('AutoMonthlyExcelLastFailure',$message,$at)
                ON CONFLICT(SettingKey) DO UPDATE SET
                  SettingValue=excluded.SettingValue,UpdatedAtUtc=excluded.UpdatedAtUtc;
                """;
            q.Parameters.AddWithValue("$message", detail);
            q.Parameters.AddWithValue("$at", _clock.GetUtcNow().ToString("O"));
            await q.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return 0;
        }, ct).ConfigureAwait(false);
    }
}
