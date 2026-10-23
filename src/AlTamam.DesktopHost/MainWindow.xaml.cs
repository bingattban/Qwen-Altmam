using System.IO;
using System.Windows;
using System.Windows.Controls;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.DesktopHost.Bridge;
using AlTamam.Infrastructure.Persistence;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AlTamam.DesktopHost;

/// <summary>
/// Real WPF executable host (04.01-04.03): embeds WebView2, maps the bundled 69-screen WebUI to the
/// exact virtual origin https://manger.adwas.example/ with Deny for unrelated origins, isolates the
/// browser profile under %LOCALAPPDATA%\AlTamam\WebView2, guards navigation and resources against any
/// external network use, recovers from failed loads, and wires the typed bridge dispatcher.
/// </summary>
public partial class MainWindow : Window
{
    private readonly WebView2 _webView = new();
    private AlTamamDbContext? _dbContext;
    private DesktopBridgeGateway? _gateway;
    private Scheduling.EightPmSchedulerService? _scheduler;

    public MainWindow()
    {
        Title = "التمام اليومي";
        // Keep the native Windows title bar visible on compact laptop displays.
        // A fixed 860 px window could start above the screen when centered.
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(1280, Math.Max(480, workArea.Width - 24));
        Height = Math.Min(860, Math.Max(360, workArea.Height - 24));
        MinWidth = Math.Min(720, workArea.Width);
        MinHeight = Math.Min(480, workArea.Height);
        ResizeMode = ResizeMode.CanResize;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        InitializeComponent();
        webViewHost.Children.Add(_webView);
        Panel.SetZIndex(_webView, 0); // keep the native Arabic startup message above the browser
        Loaded += OnLoadedOrSelfCheck;
        Closed += async (_, _) =>
        {
            try
            {
                if (_scheduler is not null)
                    await _scheduler.DisposeAsync();
            }
            finally
            {
                _dbContext?.Dispose();
            }
        };
    }

    /// <summary>
    /// 15.06 CI smoke entry: when launched with --layout-selfcheck the process validates the
    /// published layout (real bundled webui content + writable data root resolution) and exits
    /// WITHOUT opening UI/DB/WebView2 — safe for headless Windows runners. Exit code 0 = layout OK.
    /// </summary>
    internal static int RunLayoutSelfCheck()
    {
        try
        {
            ResolveBundledWebUiRoot();                       // throws if webui/index.html missing
            string probe = AppDataLayout.IsPortable
                ? AppDataLayout.DataRoot                     // portable: data beside exe
                : Path.GetFullPath(AppDataLayout.DataRoot);  // installed: %LOCALAPPDATA%\AlTamam
            AppDataLayout.AssertWritableLocation(Path.Combine(probe, "altamam.db"));
            Console.WriteLine("LAYOUT-SELFCHECK PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("LAYOUT-SELFCHECK FAIL: " + ex.Message);
            return 1;
        }
    }

    private void OnLoadedOrSelfCheck(object sender, RoutedEventArgs e)
    {
        if (Environment.GetCommandLineArgs().Contains("--layout-selfcheck", StringComparer.Ordinal))
        {
            System.Windows.Application.Current.Shutdown(RunLayoutSelfCheck());
            return;
        }
        _ = OnLoadedAsync(sender, e);
    }

    private async Task OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (Environment.GetCommandLineArgs().Contains("--webview-selfcheck", StringComparer.Ordinal))
            {
                Console.Error.WriteLine("WEBVIEW-SELFCHECK FAIL: " + ex.Message);
                System.Windows.Application.Current.Shutdown(1);
                return;
            }
            startupMessage.Text = "تعذر فتح التطبيق. تحقّق من قاعدة البيانات وبيئة WebView2.";
            MessageBox.Show(
                "تعذر تشغيل واجهة التطبيق: " + ex.Message,
                "التمام اليومي", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed record PreparedServices(
        AlTamamDbContext Database, DesktopBridgeGateway Gateway,
        Scheduling.EightPmSchedulerService Scheduler);

    /// <summary>
    /// SQLCipher key validation, migrations and bridge construction are independent of WPF.
    /// Move these potentially blocking disk operations off the UI thread. Start WebView2
    /// concurrently instead of waiting for the database to open before launching Edge.
    /// </summary>
    private static async Task<PreparedServices> PrepareServicesAsync()
    {
        string databasePath = AppDataLayout.ResolveDatabasePath();
        string key = new Infrastructure.Security.WindowsDatabaseKeyStore(
            Path.Combine(AppDataLayout.DataRoot, "db.key.protected")).LoadOrCreate(databasePath);
        var database = new AlTamamDbContext(databasePath, key);
        try
        {
            await database.InitializeAsync().ConfigureAwait(false);
            var services = new BridgeSharedServices(database);
            var dispatcher = BridgeHandlerCatalog.Create(services);
            var gateway = new DesktopBridgeGateway(dispatcher, services);
            var attendance = new AlTamam.Application.Scheduling.AutoAttendanceService(database);
            return new PreparedServices(database, gateway,
                new Scheduling.EightPmSchedulerService(attendance));
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    private async Task InitializeAsync()
    {
        bool webViewSelfCheck = Environment.GetCommandLineArgs()
            .Contains("--webview-selfcheck", StringComparer.Ordinal);
        // WebView self-check intentionally bypasses DPAPI/SQLCipher entirely.
        Task<PreparedServices>? preparing = webViewSelfCheck
            ? null : Task.Run(PrepareServicesAsync);
        try
        {
            // WebView2 profile creation and SQLCipher initialization overlap.
            string userDataFolder = Path.Combine(AppDataLayout.DataRoot, "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var options = new CoreWebView2EnvironmentOptions();
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: userDataFolder,
                options: options).ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

            if (preparing is not null)
            {
                // Do not attach the bridge or navigate until migrations are finished.
                // Assignment happens on the WPF dispatcher after the background task.
                PreparedServices ready = await preparing.ConfigureAwait(true);
                _dbContext = ready.Database;
                _gateway = ready.Gateway;
                _gateway.FolderPicker = current =>
                {
                    var dialog = new Microsoft.Win32.OpenFolderDialog
                    {
                        Title = "اختر مجلد النسخ الاحتياطي وتصدير الجداول",
                        InitialDirectory = Directory.Exists(current) ? current :
                            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    };
                    string? result = dialog.ShowDialog(this) == true ? dialog.FolderName : null;
                    return Task.FromResult(result);
                };
                _scheduler = ready.Scheduler;
            }
        }
        catch
        {
            // WebView2 can fail while the independent database task is still running.
            // Observe that task and dispose its database rather than leaking a connection.
            if (preparing is not null && _dbContext is null)
            {
                try
                {
                    PreparedServices abandoned = await preparing.ConfigureAwait(true);
                    abandoned.Database.Dispose();
                }
                catch { /* preserve the original startup failure */ }
            }
            throw;
        }

        CoreWebView2 core = _webView.CoreWebView2;

        // --- 04.02: exact virtual-host mapping of the bundled UI; deny everything else. ---
        string webUiRoot = ResolveBundledWebUiRoot();
        core.SetVirtualHostNameToFolderMapping(
            LocalWebUiOrigin.Host, webUiRoot, CoreWebView2HostResourceAccessKind.Deny);

        // --- Offline hardening: no external network, no unrelated origins. ---
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;

        core.NavigationStarting += (_, args) =>
        {
            if (!IsLocalVirtualHost(args.Uri)) args.Cancel = true;
        };
        core.FrameNavigationStarting += (_, args) =>
        {
            if (!IsLocalVirtualHost(args.Uri)) args.Cancel = true;
        };
        core.NewWindowRequested += (_, args) =>
        {
            // Popups must stay inside the same pinned origin or are denied outright.
            if (IsLocalVirtualHost(args.Uri)) args.NewWindow = core;
            else args.Handled = true;
        };
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.BasicAuthenticationRequested += (_, args) => args.Cancel = true;
        core.ClientCertificateRequested += (_, args) => args.Cancel = true;
        // Any sub-resource that is not the virtual host is blocked before it leaves the process.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (!IsLocalVirtualHost(args.Request.Uri))
                args.Response = core.Environment.CreateWebResourceResponse(
                    Stream.Null, 403, "Forbidden", "Content-Security-Policy: default-src 'none'");
        };

        // --- 04.03: failed-load recovery (reload once, then a local error page route). ---
        core.NavigationCompleted += async (_, args) =>
        {
            if (webViewSelfCheck) return;
            if (args.IsSuccess)
            {
                startupPanel.Visibility = Visibility.Collapsed;
                _scheduler?.Start(); // start catch-up after UI is ready; never delay first paint
                return;
            }
            if (!_recoveredOnce)
            {
                _recoveredOnce = true;
                await Task.Delay(500).ConfigureAwait(true);
                core.Reload();
            }
            else
            {
                MessageBox.Show(
                    "تعذر تحميل واجهة التطبيق المحلية. حالة WebView2: " + args.WebErrorStatus,
                    "التمام اليومي", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        if (webViewSelfCheck)
        {
            await CheckActualWebViewNavigationAsync(core).ConfigureAwait(true);
            return;
        }

        // --- 04.04: attach the typed bridge after the pipe exists. ---
        _gateway!.Attach(core);

        core.Navigate(LocalWebUiOrigin.EntryPointUrl);
    }

    private bool _recoveredOnce;

    /// <summary>
    /// Real Windows-only navigation smoke test: unlike --layout-selfcheck it creates
    /// WebView2, resolves the virtual host, loads the bundled document, and verifies
    /// its title without requiring a database or credentials.
    /// </summary>
    private static async Task CheckActualWebViewNavigationAsync(CoreWebView2 core)
    {
        var completion = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs> onNavigation =
            (_, args) => completion.TrySetResult(args);
        core.NavigationCompleted += onNavigation;
        try
        {
            core.Navigate(LocalWebUiOrigin.EntryPointUrl);
            CoreWebView2NavigationCompletedEventArgs result =
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            if (!result.IsSuccess)
                throw new InvalidOperationException(
                    "Local UI navigation failed: " + result.WebErrorStatus);
            string title = await core.ExecuteScriptAsync("document.title").ConfigureAwait(true);
            if (!title.Contains("التمام اليومي", StringComparison.Ordinal))
                throw new InvalidOperationException("Local UI did not load the expected document.");
            Console.WriteLine("WEBVIEW-SELFCHECK PASS");
            System.Windows.Application.Current.Shutdown(0);
        }
        finally
        {
            core.NavigationCompleted -= onNavigation;
        }
    }

    /// <summary>Bundled WebUI lives next to the executable under webui/ (copied by the csproj).</summary>
    private static string ResolveBundledWebUiRoot()
    {
        string candidate = Path.Combine(AppDataLayout.ResourceRoot, "webui");
        if (!Directory.Exists(candidate) || !File.Exists(Path.Combine(candidate, "index.html")))
            throw new DirectoryNotFoundException(
                "المحتوى المضمّن للواجهة (webui/index.html) مفقود؛ أعد تثبيت التطبيق.");
        return candidate;
    }

    private static bool IsLocalVirtualHost(string uriString)
        => Uri.TryCreate(uriString, UriKind.Absolute, out Uri? uri)
           && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
           && string.Equals(uri.Host, LocalWebUiOrigin.Host, StringComparison.OrdinalIgnoreCase);
}

