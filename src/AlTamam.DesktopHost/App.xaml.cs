using System.Linq;
using System.Windows;
// NOTE: no StartupUri in App.xaml — window creation is explicit here so the
// --layout-selfcheck CI path can exit before any UI is constructed (15.06).

namespace AlTamam.DesktopHost;

/// <summary>Application entry point for the real Windows desktop host (04.01).</summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 15.06 CI smoke: headless layout self-check exits with its own code and never
        // constructs the UI, database, or WebView2 environment.
        if (e.Args.Contains("--layout-selfcheck", StringComparer.Ordinal))
        {
            Shutdown(AlTamam.DesktopHost.MainWindow.RunLayoutSelfCheck());
            return;
        }

        // WPF initializes App.xaml through its generated application entry point.
        // Do not invoke a second InitializeComponent() from OnStartup.
        MainWindow window = new();
        MainWindow = window;
        window.Show();
    }
}
