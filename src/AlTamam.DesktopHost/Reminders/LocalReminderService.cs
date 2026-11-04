using System.Globalization;
using System.IO;
using System.Media;
using System.Text;
using System.Text.Json;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;

namespace AlTamam.DesktopHost.Reminders;

/// <summary>
/// Optional local-only personal reminders. Never opens SQLCipher or writes any audit ledger entries,
/// or changes the application scheduler. Calls require a live native user session.
/// </summary>
internal sealed class LocalReminderService
{
    private const string Channel = "altamam.reminders.v1";
    private const string Stamp = "yyyy-MM-ddTHH:mm";
    private const string SnoozeStamp = "yyyy-MM-ddTHH:mm:ss";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _path = Path.Combine(AppDataLayout.DataRoot, "Reminders", "reminders.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DateTime _startedLocal = DateTime.Now;

    public sealed class ReminderFile
    {
        public ReminderFile() { }
        public int SchemaVersion { get; set; } = 1;
        public List<Reminder> Items { get; set; } = [];
    }

    public sealed class Reminder
    {
        public Reminder() { }
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Subject { get; set; } = "";
        public string DueLocal { get; set; } = "";
        public string? SnoozedUntilLocal { get; set; }
        public bool Delivered { get; set; }
        public bool Completed { get; set; }
    }

    private static bool GetText(JsonElement payload, string key, out string value)
    {
        value = "";
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty(key, out var v) ||
            v.ValueKind != JsonValueKind.String) return false;
        value = v.GetString()?.Trim() ?? "";
        return true;
    }

    private static bool TryLocalDate(string value, out DateTime date) =>
        DateTime.TryParseExact(value, [Stamp, SnoozeStamp], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);

    private static object Error(string message) => new { ok = false, error = message };

    private static bool Authorized(BridgePrincipal? principal) =>
        principal?.Role.ToString() is "GeneralAdministration" or "OrdinaryAdministration" or "Supervisor";

    private async Task<ReminderFile> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new ReminderFile();
        var file = new FileInfo(_path);
        if (file.Length > 1024 * 1024)
            throw new InvalidDataException("Reminder file is too large.");
        var contents = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
        var data = JsonSerializer.Deserialize<ReminderFile>(contents, JsonOptions)
            ?? throw new InvalidDataException("Invalid reminder JSON.");
        if (data.SchemaVersion != 1 || data.Items is null || data.Items.Count > 200 ||
            data.Items.Any(item => item is null || !Guid.TryParseExact(item.Id, "N", out _) ||
                item.Name.Length is < 1 or > 120 ||
                item.Subject.Length is < 1 or > 2000 ||
                !TryLocalDate(item.DueLocal, out _) ||
                (item.SnoozedUntilLocal is not null &&
                 !TryLocalDate(item.SnoozedUntilLocal, out _))) ||
            data.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != data.Items.Count)
            throw new InvalidDataException("Invalid reminder document; original file preserved.");
        return data;
    }

    private async Task SaveAsync(ReminderFile data, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, ".reminders-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Returns null only for messages belonging to the canonical application bridge.</summary>
    public async Task<string?> TryHandleAsync(string raw, string origin, bool isMainFrame,
        Func<string, Task<BridgePrincipal?>> authenticate, CancellationToken ct = default)
    {
        if (raw.Length > 24 * 1024) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !GetText(root, "channel", out var channel) || channel != Channel)
                return null;

            GetText(root, "requestId", out var requestId);
            object result;
            if (!isMainFrame || !WebView2BridgeDispatcher.IsAllowedOrigin(origin) ||
                !GetText(root, "sessionNonce", out var nonce) ||
                !Authorized(await authenticate(nonce).ConfigureAwait(false)))
                result = Error("يجب تسجيل الدخول بحساب مخول قبل إدارة التنبيهات.");
            else if (!GetText(root, "command", out var command) ||
                     !root.TryGetProperty("payload", out var payload) ||
                     payload.ValueKind != JsonValueKind.Object)
                result = Error("طلب تنبيهات غير صالح.");
            else
                result = await ExecuteAsync(command, payload, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { channel = Channel, requestId, result }, JsonOptions);
        }
        catch (JsonException)
        {
            // A malformed canonical request remains the sole responsibility of the normal dispatcher.
            return null;
        }
        catch (Exception)
        {
            // No filesystem paths, exception text, or protected user data leave the host.
            return JsonSerializer.Serialize(new
            {
                channel = Channel, requestId = ReadRequestIdSafely(raw),
                result = Error("تعذر قراءة أو حفظ ملف التنبيهات المحلي؛ لم تتغير بيانات التمام.")
            }, JsonOptions);
        }
    }

    private static string ReadRequestIdSafely(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return GetText(doc.RootElement, "requestId", out var id) ? id : "";
        }
        catch (JsonException) { return ""; }
    }

    private async Task<object> ExecuteAsync(string command, JsonElement payload, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var data = await ReadAsync(ct).ConfigureAwait(false);
            if (command == "list")
                return new { ok = true, data = new { items = data.Items } };

            if (command == "poll")
            {
                var now = DateTime.Now;
                // A delivered but unacknowledged alert remains visible after
                // navigating between main and standalone pages or restarting.
                // Only the FIRST timely delivery rings; subsequent presentations
                // are silent and must never steal focus or affect business work.
                var notifications = data.Items.Where(item =>
                    !item.Completed &&
                    TryLocalDate(item.SnoozedUntilLocal ?? item.DueLocal, out var due) &&
                    due <= now).OrderBy(item => item.SnoozedUntilLocal ?? item.DueLocal,
                        StringComparer.Ordinal).ToList();
                if (notifications.Count == 0)
                    return new { ok = true, data = new { notifications = Array.Empty<object>() } };
                var shown = notifications.Select(item =>
                {
                    TryLocalDate(item.SnoozedUntilLocal ?? item.DueLocal, out var due);
                    return new
                    {
                        item.Id, item.Name, item.Subject, item.DueLocal,
                        silent = item.Delivered || due < _startedLocal
                    };
                }).ToArray();
                bool saveDeliveryState = notifications.Any(item => !item.Delivered);
                foreach (var item in notifications) item.Delivered = true;
                if (saveDeliveryState) await SaveAsync(data, ct).ConfigureAwait(false);
                if (shown.Any(item => !item.silent))
                {
                    try { SystemSounds.Exclamation.Play(); } catch { /* never block operations */ }
                }
                return new { ok = true, data = new { notifications = shown } };
            }

            GetText(payload, "id", out var id);
            var existing = data.Items.FirstOrDefault(item => item.Id == id);
            if (command == "upsert")
            {
                if (!GetText(payload, "name", out var name) || name.Length is < 1 or > 120 ||
                    !GetText(payload, "subject", out var subject) || subject.Length is < 1 or > 2000 ||
                    !GetText(payload, "dueLocal", out var local) || !TryLocalDate(local, out var due) ||
                    due <= DateTime.Now)
                    return Error("أدخل اسماً وموضوعاً وموعداً مستقبلياً صحيحاً للتنبيه.");
                if (id.Length > 0 && existing is null)
                    return Error("هذا التنبيه لم يعد موجوداً؛ حدّث القائمة.");
                if (existing is null)
                {
                    if (data.Items.Count >= 200) return Error("تم بلوغ الحد الأقصى للتنبيهات (200).");
                    existing = new Reminder { Id = Guid.NewGuid().ToString("N") };
                    data.Items.Add(existing);
                }
                existing.Name = name;
                existing.Subject = subject;
                existing.DueLocal = local;
                existing.SnoozedUntilLocal = null;
                existing.Delivered = false;
                existing.Completed = false;
            }
            else if (existing is null)
                return Error("هذا التنبيه غير موجود؛ حدّث القائمة.");
            else if (command == "delete")
                data.Items.Remove(existing);
            else if (command == "dismiss")
            {
                existing.Completed = true;
                existing.Delivered = true;
            }
            else if (command == "snooze")
            {
                if (existing.Completed) return Error("لا يمكن تأجيل تنبيه منتهٍ.");
                existing.SnoozedUntilLocal = DateTime.Now.AddMinutes(5).ToString(SnoozeStamp,
                    CultureInfo.InvariantCulture);
                existing.Delivered = false;
            }
            else
                return Error("أمر تنبيهات غير معروف.");

            await SaveAsync(data, ct).ConfigureAwait(false);
            return new { ok = true, data = new { items = data.Items } };
        }
        finally { _gate.Release(); }
    }
}
