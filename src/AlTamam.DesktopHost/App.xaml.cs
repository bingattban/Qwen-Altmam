using System.Linq;
using System.Threading;
using System.Windows;
// NOTE: no StartupUri in App.xaml — window creation is explicit here so the
// --layout-selfcheck CI path can exit before any UI is constructed (15.06).

namespace AlTamam.DesktopHost;

/// <summary>Application entry point for the real Windows desktop host (04.01).</summary>
public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private bool _ownsSingleInstance;

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

        // Prevent two interactive instances on the same Windows session from
        // editing the same SQLCipher database concurrently. The headless CI
        // self-check above deliberately bypasses this lock.
        _singleInstance = new Mutex(initiallyOwned: true,
            name: @"Local\AlTamamDaily_DesktopHost", createdNew: out bool acquired);
        if (!acquired)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            MessageBox.Show("التمام اليومي يعمل بالفعل. أغلق النسخة المفتوحة قبل تشغيل نسخة أخرى.",
                "التمام اليومي", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        _ownsSingleInstance = true;

        // WPF initializes App.xaml through its generated application entry point.
        // Do not invoke a second InitializeComponent() from OnStartup.
        MainWindow window = new();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstance)
        {
            _singleInstance?.ReleaseMutex();
            _ownsSingleInstance = false;
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
