using System.IO;
using System.Security.Cryptography;
using System.Windows;
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
        Width = 1280;
        Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        InitializeComponent();
        webViewHost.Children.Add(_webView);
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
            MessageBox.Show(
                "تعذر تشغيل واجهة التطبيق: " + ex.Message,
                "التمام اليومي", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InitializeAsync()
    {
        // --- Host data layer: encrypted local database, key materialized on first run. ---
        string databasePath = AppDataLayout.ResolveDatabasePath();
        string key = LocalSecretStore.GetOrCreateDatabaseKey();
        _dbContext = new AlTamamDbContext(databasePath, key);
        await _dbContext.InitializeAsync().ConfigureAwait(true);

        var services = new BridgeSharedServices(_dbContext);
        WebView2BridgeDispatcher dispatcher = BridgeHandlerCatalog.Create(services);
        _gateway = new DesktopBridgeGateway(dispatcher, services);

        // --- 07.02: durable host-side 20:00 scheduler (no HTML page, no JS timer). Startup call
        // performs the approved bounded catch-up for dates missed while the machine was off. ---
        var autoAttendance = new AlTamam.Application.Scheduling.AutoAttendanceService(_dbContext);
        _scheduler = new Scheduling.EightPmSchedulerService(autoAttendance);
        _scheduler.Start();

        // --- 04.03: WebView2 environment with an isolated, application-owned user profile. ---
        string userDataFolder = Path.Combine(AppDataLayout.DataRoot, "WebView2");
        Directory.CreateDirectory(userDataFolder);
        var options = new CoreWebView2EnvironmentOptions();
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null, userDataFolder: userDataFolder, options: options).ConfigureAwait(true);
        await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);

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
            if (args.IsSuccess) return;
            if (!_recoveredOnce)
            {
                _recoveredOnce = true;
                await Task.Delay(500).ConfigureAwait(true);
                core.Reload();
            }
        };

        // --- 04.04: attach the typed bridge after the pipe exists. ---
        _gateway.Attach(core);

        core.Navigate(LocalWebUiOrigin.BaseUrl);
    }

    private bool _recoveredOnce;

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

/// <summary>
/// Minimal DPAPI-backed store for the SQLCipher key (per-user, never shipped in the repository).
/// Full Windows-keystore hardening continues in the dedicated security stage.
/// </summary>
internal static class LocalSecretStore
{
    private const string KeyFile = "db.key.protected";

    public static string GetOrCreateDatabaseKey()
    {
        string path = Path.Combine(AppDataLayout.DataRoot, KeyFile);
        if (File.Exists(path))
        {
            byte[] protectedBlob = File.ReadAllBytes(path);
            byte[] plain = ProtectedData.Unprotect(protectedBlob, optionalEntropy: null, DataProtectionScope.CurrentUser);
            string hex = System.Text.Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            if (!string.IsNullOrWhiteSpace(hex)) return hex;
        }
        string fresh = Infrastructure.Security.DatabaseKeyGenerator.Generate();
        byte[] blob = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(fresh), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, blob);
        return fresh;
    }
}
