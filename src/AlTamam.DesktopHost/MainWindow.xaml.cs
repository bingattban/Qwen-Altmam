using System.IO;
using System.Windows;
using System.Windows.Controls;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.DesktopHost.Bridge;
using AlTamam.DesktopHost.Presentation;
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
    private readonly StartupViewModel _startupViewModel = new();
    private AlTamamDbContext? _dbContext;
    private DesktopBridgeGateway? _gateway;
    private Scheduling.EightPmSchedulerService? _scheduler;
    private Scheduling.AutomaticBackupSchedulerService? _backupScheduler;

    // Diagnostics are written only for the explicit CI smoke-test flag; production
    // startup never creates a trace file or changes its persistent browser profile.
    internal static void TraceWebViewSelfCheck(string phase)
    {
        if (!Environment.GetCommandLineArgs().Contains("--webview-selfcheck",
                StringComparer.Ordinal)) return;
        string? tracePath = Environment.GetEnvironmentVariable("ALTAMAM_WEBVIEW_SELFCHECK_TRACE");
        if (string.IsNullOrWhiteSpace(tracePath)) return;
        try
        {
            File.AppendAllText(tracePath,
                DateTimeOffset.UtcNow.ToString("O") + " " + phase + Environment.NewLine);
        }
        catch (IOException) { /* diagnostics must not break the navigation smoke test */ }
        catch (UnauthorizedAccessException) { /* same: fail from the real WebView2 result */ }
    }

    public MainWindow()
    {
        Title = "التمام اليومي";
        Icon = new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/Assets/AlTamam.ico", UriKind.Absolute));
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
        DataContext = _startupViewModel;
        webViewHost.Children.Add(_webView);
        Panel.SetZIndex(_webView, 0); // keep the native Arabic startup message above the browser
        Loaded += OnLoadedOrSelfCheck;
        // Native WPF keyboard/mouse/touch events are real user input, unlike
        // dispatcher timers or background WebUI refreshes. Browser input is
        // independently observed via trusted WebView2 DOM events.
        PreviewKeyDown += (_, _) => RefreshIdleFromNativeInput();
        PreviewMouseDown += (_, _) => RefreshIdleFromNativeInput();
        PreviewMouseWheel += (_, _) => RefreshIdleFromNativeInput();
        PreviewTouchDown += (_, _) => RefreshIdleFromNativeInput();
        Closed += async (_, _) =>
        {
            try
            {
                if (_scheduler is not null)
                    await _scheduler.DisposeAsync();
                if (_backupScheduler is not null)
                    await _backupScheduler.DisposeAsync();
            }
            finally
            {
                _dbContext?.Dispose();
            }
        };
    }

    private DateTimeOffset _lastNativeActivityPing=DateTimeOffset.MinValue;

    private async void RefreshIdleFromNativeInput()
    {
        if(_gateway is null)return;
        DateTimeOffset now=DateTimeOffset.UtcNow;
        if(now-_lastNativeActivityPing < TimeSpan.FromSeconds(1))return;
        _lastNativeActivityPing=now;
        try {await _gateway.RecordNativeInputAsync().ConfigureAwait(true);}
        catch { /* Never crash UI on an idle-activity update. The server still expires. */ }
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
            // Both distribution types use the unchanged Windows-user AppData path.
            // --layout-selfcheck must not create or move a database.
            string probe = Path.GetFullPath(AppDataLayout.DataRoot);
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
            TraceWebViewSelfCheck("WPF window loaded");
            await InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (Environment.GetCommandLineArgs().Contains("--webview-selfcheck", StringComparer.Ordinal))
            {
                TraceWebViewSelfCheck("FAIL " + ex.GetType().Name + ": " + ex.Message);
                Console.Error.WriteLine("WEBVIEW-SELFCHECK FAIL: " + ex.Message);
                System.Windows.Application.Current.Shutdown(1);
                return;
            }
            // Key recovery and invalid storage paths have different remedies.
            // Never show a raw DPAPI key, SQLCipher secret or untrusted path as
            // part of a low-level exception in the user-facing Windows dialog.
            string? recoveryMessage =
                AlTamam.Infrastructure.Security.DatabaseStartupFailureClassifier
                    .UserMessage(ex);
            _startupViewModel.Message = recoveryMessage ??
                "تعذر فتح التطبيق. تحقّق من قاعدة البيانات وبيئة WebView2.";
            MessageBox.Show(
                recoveryMessage ?? ("تعذر تشغيل واجهة التطبيق: " + ex.Message),
                "التمام اليومي", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed record PreparedServices(
        AlTamamDbContext Database, DesktopBridgeGateway Gateway,
        Scheduling.EightPmSchedulerService Scheduler,
        Scheduling.AutomaticBackupSchedulerService BackupScheduler);

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
        // The desktop application already owns the single-instance mutex here.
        // Reconcile an interrupted encrypted restore BEFORE opening any DB
        // connection, applying migrations, starting scheduled jobs or attaching
        // WebView2. Unknown states fail CLOSED without touching user records.
        if (File.Exists(databasePath) ||
            File.Exists(Path.Combine(AppDataLayout.DataRoot,
                ".altamam-restore-activation.pending.json")))
        {
            await new OfflineRestoreSwitch()
                .ReconcileAtStartupAsync(databasePath, key).ConfigureAwait(false);
        }
        var database = new AlTamamDbContext(databasePath, key);
        try
        {
            await database.InitializeAsync().ConfigureAwait(false);
            var services = new BridgeSharedServices(database);
            await services.RevokeOrphanedSessionsOnStartupAsync().ConfigureAwait(false);
            var dispatcher = BridgeHandlerCatalog.Create(services);
            var gateway = new DesktopBridgeGateway(dispatcher, services);
            var attendance = new AlTamam.Application.Scheduling.AutoAttendanceService(database);
            return new PreparedServices(database, gateway,
                new Scheduling.EightPmSchedulerService(attendance,
                    tokenRetention: new AlTamam.Application.Scheduling.PreviewTokenRetentionService(database)),
                new Scheduling.AutomaticBackupSchedulerService(
                    new AlTamam.Application.Scheduling.AutomaticBackupService(database)));
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Offline-first WebView2 installation: the signed Microsoft standalone
    /// installer ships in BOTH the portable ZIP and the installed directory.
    /// No CDN or online bootstrapper runs on customer computers.
    /// </summary>
    private static async Task EnsureBundledWebView2Async()
    {
        try
        {
            if(!string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString()))
                return;
        }
        catch (WebView2RuntimeNotFoundException) { /* offline installer below */ }

        string installer=Path.Combine(AppContext.BaseDirectory,"Prerequisites",
            "MicrosoftEdgeWebView2RuntimeInstaller.exe");
        if(!File.Exists(installer))
            throw new FileNotFoundException(
                "مكون WebView2 غير مثبت وملف تثبيته المحلي غير موجود في حزمة التطبيق.",installer);
        using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName=installer,
            Arguments="/silent /install",
            UseShellExecute=false,
            CreateNoWindow=true
        })??throw new InvalidOperationException("تعذر بدء تثبيت WebView2 المحلي.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        if(process.ExitCode is not (0 or 3010))
            throw new InvalidOperationException(
                "فشل تثبيت WebView2 المحلي؛ رمز الخروج: "+process.ExitCode);
        if(string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString()))
            throw new InvalidOperationException("لم يتوفر WebView2 بعد التثبيت المحلي.");
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
            // The WebView2 runtime is included and installed locally if missing.
            // Do not touch the network from installed production code.
            if(!webViewSelfCheck)await EnsureBundledWebView2Async().ConfigureAwait(true);
            // WebView2 profile creation and SQLCipher initialization overlap.
            // A smoke test must not lock, migrate, or reuse the operator's
            // persistent WebView2 profile. Retry gets a new temporary profile.
            string userDataFolder = webViewSelfCheck
                ? Path.Combine(Path.GetTempPath(),
                    "AlTamam-WebView2-SelfCheck-" + Guid.NewGuid().ToString("N"))
                : Path.Combine(AppDataLayout.DataRoot, "WebView2");
            TraceWebViewSelfCheck("Creating isolated WebView2 profile");
            Directory.CreateDirectory(userDataFolder);
            var options = new CoreWebView2EnvironmentOptions();
            TraceWebViewSelfCheck("Creating CoreWebView2Environment");
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: userDataFolder,
                options: options).ConfigureAwait(true);
            TraceWebViewSelfCheck("Ensuring CoreWebView2 control");
            await _webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            TraceWebViewSelfCheck("CoreWebView2 ready");

            if (preparing is not null)
            {
                // Do not attach the bridge or navigate until migrations are finished.
                // Assignment happens on the WPF dispatcher after the background task.
                PreparedServices ready = await preparing.ConfigureAwait(true);
                _dbContext = ready.Database;
                _gateway = ready.Gateway;
                // A bridge handler may run on a worker thread. Marshal the native
                // WPF directory picker to its owning STA Dispatcher, never a pool thread.
                _gateway.FolderPicker = current => Dispatcher.InvokeAsync(() =>
                {
                    var dialog = new Microsoft.Win32.OpenFolderDialog
                    {
                        Title = "اختر مجلد النسخ الاحتياطي وتصدير الجداول",
                        InitialDirectory = Directory.Exists(current) ? current :
                            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    };
                    return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
                }).Task;
                _gateway.BackupFilePicker = current => Dispatcher.InvokeAsync(() =>
                {
                    var dialog=new Microsoft.Win32.OpenFileDialog
                    {
                        Title="اختر نسخة احتياطية مشفرة للتحقق منها",
                        Filter="Encrypted AlTamam backups (*.dbbackup)|*.dbbackup|Encrypted SQLite (*.db)|*.db",
                        CheckFileExists=true,CheckPathExists=true,
                        Multiselect=false,RestoreDirectory=true,
                        InitialDirectory=Directory.Exists(current)?current:
                            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    };
                    return dialog.ShowDialog(this)==true?dialog.FileName:null;
                }).Task;
                _scheduler = ready.Scheduler;
                _backupScheduler = ready.BackupScheduler;
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
                _startupViewModel.PanelVisibility = Visibility.Collapsed;
                _scheduler?.Start(); // start catch-up after UI is ready; never delay first paint
                _backupScheduler?.Start(); // independent verified automatic snapshots
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
            TraceWebViewSelfCheck("Navigating to bundled offline WebUI");
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

    /// <summary>CI-only real WPF/WebView2 RTL/modal interaction check, with an
    /// isolated temporary profile and no SQLCipher, bridge, or user data.</summary>
    private static async Task CheckActualWebViewModalInteractionsAsync(CoreWebView2 core)
    {
        bool ready = false;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            string available = await core.ExecuteScriptAsync(
                "Boolean(window.AltamamDialog && window.AltamamDialog.form && document.getElementById('appModalBackdrop'))")
                .ConfigureAwait(true);
            if (available == "true") { ready = true; break; }
            await Task.Delay(200).ConfigureAwait(true);
        }
        if (!ready)
            throw new InvalidOperationException("UI-E2E: offline modal controller did not initialize.");

        string result = await core.ExecuteScriptAsync("""
            (() => {
                const check = (value, name) => { if (!value) throw new Error("UI-E2E: " + name); };
                check(location.origin === "https://manger.adwas.example", "offline origin");
                check(document.documentElement.dir === "rtl", "Arabic RTL document");
                check(document.querySelector("#appViewport"), "live application viewport");
                for (const [id, label] of [
                    ["dashboard", "الرئيسية"], ["monthly", "التمام الشهري"],
                    ["admin", "الإدارة"], ["settings", "الإعدادات"]
                ]) {
                    const nav = document.getElementById("navBtn-" + id);
                    check(nav && nav.querySelector("span")?.textContent === label, "navigation " + id);
                }
                check([...document.scripts].every(s => !s.src ||
                    new URL(s.src).origin === location.origin), "local scripts only");
                const dialog = window.AltamamDialog;
                const backdrop = document.getElementById("appModalBackdrop");
                check(!backdrop.classList.contains("open"), "modal initially closed");
                const opener = document.createElement("button");
                opener.type = "button";
                opener.textContent = "UI-E2E";
                document.body.appendChild(opener);
                opener.focus();
                dialog.form([{name:"e2eName",label:"اسم الاختبار",type:"text",
                    required:true,minLength:3}], {title:"اختبار نافذة الإدخال",size:"md"});
                check(backdrop.classList.contains("open"), "form opens as modal");
                check(backdrop.getAttribute("aria-modal") === "true", "dialog accessibility");
                check(backdrop.classList.contains("dialog-md"), "medium modal size");
                const field = document.querySelector('#appModalBody input[name="e2eName"]');
                const accept = document.querySelector("#appModalBody .app-dialog-actions .btn-primary");
                check(field && accept, "live modal controls");
                check(getComputedStyle(backdrop).display !== "none", "modal visible");
                const bounds = backdrop.querySelector(".modal-box").getBoundingClientRect();
                check(bounds.width > 0 &&
                    Math.abs(bounds.left + bounds.width / 2 - innerWidth / 2) < 32,
                    "centered WPF WebView2 dialog");
                accept.click();
                check(backdrop.classList.contains("open") &&
                    document.querySelector("#appModalBody .app-dialog-error").textContent,
                    "required field rejects empty input");
                field.value = "Valid E2E";
                accept.click();
                check(!backdrop.classList.contains("open"), "valid form closes");
                check(document.activeElement === opener, "form restores opener focus");
                dialog.confirm("تأكيد الاختبار",{size:"sm"});
                check(backdrop.classList.contains("open") &&
                    backdrop.classList.contains("dialog-sm"), "confirmation size");
                document.dispatchEvent(new KeyboardEvent("keydown",
                    {key:"Escape", bubbles:true,cancelable:true}));
                check(!backdrop.classList.contains("open"), "Escape dismisses dialog");
                check(document.activeElement === opener, "Escape restores focus");
                opener.remove();
                return "UI-E2E PASS";
            })()
            """).ConfigureAwait(true);
        if (result != "\"UI-E2E PASS\"")
            throw new InvalidOperationException("UI-E2E: real browser interaction failed: " + result);
        TraceWebViewSelfCheck("UI-E2E PASS real Windows RTL/modal/form/validation/Escape/focus");
    }

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
            if (Environment.GetCommandLineArgs().Contains("--webview-ui-e2e", StringComparer.Ordinal))
                await CheckActualWebViewModalInteractionsAsync(core).ConfigureAwait(true);
            TraceWebViewSelfCheck("PASS bundled offline WebUI title verified");
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

