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

    public DesktopBridgeGateway(WebView2BridgeDispatcher dispatcher, BridgeSharedServices services)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(services);
        _dispatcher = dispatcher;
        _services = services;
    }

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
            // The postMessage pipe only delivers from documents rendered inside this WebView, whose
            // origin is pinned to the bundled virtual host by SetVirtualHostNameToFolderMapping plus
            // the NavigationStarting/FrameNavigation guards. Pass the page URI for the strict check.
            string originUri = _core.Source ?? LocalWebUiOrigin.BaseUrl;
            var result = await _dispatcher.DispatchAsync(
                raw,
                originUri,
                isMainFrame: true,
                nonce => _services.ResolveSessionAsync(nonce)).ConfigureAwait(true);

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
