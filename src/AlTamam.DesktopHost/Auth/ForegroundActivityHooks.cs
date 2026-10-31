using System.Windows;

namespace AlTamam.DesktopHost.Auth;

/// <summary>Only real foreground WPF input may trigger native session renewal.
/// WebView2 Chromium input remains separately OS-verified by DesktopBridgeGateway.</summary>
public static class ForegroundActivityHooks
{
    public static void Attach(Window window, Action recordActualInput)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(recordActualInput);
        void OnInput() { if (window.IsActive) recordActualInput(); }
        window.PreviewKeyDown += (_, _) => OnInput();
        window.PreviewMouseDown += (_, _) => OnInput();
        window.PreviewMouseWheel += (_, _) => OnInput();
        window.PreviewTouchDown += (_, _) => OnInput();
    }
}
