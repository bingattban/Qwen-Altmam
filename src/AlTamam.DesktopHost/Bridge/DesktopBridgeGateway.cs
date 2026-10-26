using System.Runtime.InteropServices;
using System.Text.Json;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using Microsoft.Web.WebView2.Core;

namespace AlTamam.DesktopHost.Bridge;

/// <summary>
/// Host-side adapter that connects the WebView2 message pipe to the typed
/// <see cref="WebView2BridgeDispatcher"/>. Every frame is checked against the canonical origin and
/// main-frame rules before parsing; responses are serialized with the canonical camelCase envelope.
/// </summary>
public sealed class DesktopBridgeGateway
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WebView2BridgeDispatcher _dispatcher;
    private readonly BridgeSharedServices _services;
    private CoreWebView2? _core;

    // Genuine browser mouse/key/touch input is visible to Windows via
    // GetLastInputInfo even when WebView2 does not bubble input to WPF.
    // A JavaScript flag is NOT sufficient evidence of human activity.
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Tick; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private static bool IsRecentPhysicalInputIntoForegroundApp()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero ||
            GetWindowThreadProcessId(window, out uint processId) == 0 ||
            processId != (uint)Environment.ProcessId)
            return false;
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref input)) return false;
        // Both are 32-bit Windows uptime ticks; unsigned subtraction is
        // wrap-safe, including across the 49-day tick counter rollover.
        uint elapsed = unchecked((uint)Environment.TickCount - input.Tick);
        return elapsed <= 2_000;
    }

    private static string? RequestedHumanActivitySessionNonce(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if(root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("action", out var action) ||
                action.ValueKind != JsonValueKind.String ||
                action.GetString() != "GetCurrentSession" ||
                !root.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("userActivity", out var activity) ||
                activity.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("sessionNonce", out var nonce) ||
                nonce.ValueKind != JsonValueKind.String)
                return null;
            var sessionNonce=nonce.GetString();
            return BridgeProtocol.IsValidSessionNonceFormat(sessionNonce) ? sessionNonce : null;
        }
        catch (JsonException) { return null; }
    }

    public Func<string?,Task<string?>>? FolderPicker
    { get => _services.PickOutputDirectoryAsync; set => _services.PickOutputDirectoryAsync = value; }
    public Func<string?,Task<string?>>? BackupFilePicker
    { get => _services.PickBackupFileAsync; set => _services.PickBackupFileAsync = value; }

    public DesktopBridgeGateway(WebView2BridgeDispatcher dispatcher, BridgeSharedServices services)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(services);
        _dispatcher = dispatcher;
        _services = services;
    }

    public Task RecordNativeInputAsync(CancellationToken ct=default)
        => _services.RefreshActiveSessionFromNativeInputAsync(ct);

    /// <summary>Attaches the gateway to one CoreWebView2 instance (postMessage pipe only).</summary>
    public void Attach(CoreWebView2 core)
    {
        ArgumentNullException.ThrowIfNull(core);
        _core = core;
        core.WebMessageReceived += OnWebMessageReceived;
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_core is null) return;
        try
        {
            string raw = e.TryGetWebMessageAsString();
            // Source MUST come from the actual web-message sender (e.Source),
            // never the outer WebView's trusted Source as a substitute for a
            // message sent by an injected/foreign child frame.
            string originUri = e.Source;
            bool isMainFrame = string.Equals(originUri, _core.Source,
                StringComparison.OrdinalIgnoreCase);
            var result = await _dispatcher.DispatchAsync(
                raw,
                originUri,
                isMainFrame,
                nonce => _services.ResolveSessionAsync(nonce)).ConfigureAwait(true);

            // A successful authenticated session check is not enough to
            // refresh inactivity: verify actual recent Windows user input.
            // Neither timers nor forged payload.userActivity alone can extend it.
            // Explicitly renew only the nonce authenticated by THIS request.
            // A delayed old-user message cannot renew a newly logged-in account.
            string? activityNonce=result.Response.Success
                ? RequestedHumanActivitySessionNonce(raw) : null;
            if (activityNonce is not null && IsRecentPhysicalInputIntoForegroundApp())
                await _services.RefreshSessionIdleDeadlineAsync(activityNonce).ConfigureAwait(true);

            string json = JsonSerializer.Serialize(result.Response, ResponseJsonOptions);
            _core.PostWebMessageAsString(json);
        }
        catch (Exception)
        {
            // Never leak exception details into the UI; answer with a canonical failure envelope.
            _core.PostWebMessageAsString(JsonSerializer.Serialize(
                new BridgeResponse("", false, null, BridgeErrorCodes.ValidationError, "تعذر معالجة الرسالة."),
                ResponseJsonOptions));
        }
    }

    /// <summary>Pushes an unsolicited bridge event (e.g. UiInvalidation broadcast) to the page.</summary>
    public void BroadcastEvent(object payload)
    {
        if (_core is null) return;
        string json = JsonSerializer.Serialize(payload, ResponseJsonOptions);
        _core.PostWebMessageAsString(json);
    }
}
