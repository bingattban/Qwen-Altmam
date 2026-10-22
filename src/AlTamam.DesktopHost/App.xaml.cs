using System.Linq;
using System.Windows;
// NOTE: no StartupUri in App.xaml — window creation is explicit here so the
// --layout-selfcheck CI path can exit before any UI is constructed (15.06).

namespace AlTamam.DesktopHost;

/// <summary>Application entry point for the real Windows desktop host (04.01).</summary>
public partial class App : System.Windows.Application
{
    private bool initialized;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 15.06 CI smoke: headless layout self-check exits with its own code and never
        // constructs the UI, database, or WebView2 environment.
        if (e.Args.Contains("--layout-selfcheck", StringComparer.Ordinal))
        {
            Shutdown(MainWindow.RunLayoutSelfCheck());
            return;
        }

        // Generated App.g.cs provides InitializeComponent(); runs once before any window opens.
        if (!initialized)
        {
            InitializeComponent();
            initialized = true;
        }
        MainWindow window = new();
        MainWindow = window;
        window.Show();
    }
}
