#!/usr/bin/env python3
"""Guard real x86/x64 WebView2 smoke tests against no-op or unbounded bypasses."""
from pathlib import Path
import re
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1]
HOST = (ROOT / "src/AlTamam.DesktopHost/MainWindow.xaml.cs").read_text(encoding="utf-8")
APP = (ROOT / "src/AlTamam.DesktopHost/App.xaml.cs").read_text(encoding="utf-8")
RELEASE = (ROOT / ".github/workflows/windows-release.yml").read_text(encoding="utf-8")
CORE = (ROOT / ".github/workflows/windows-ci.yml").read_text(encoding="utf-8")


class WebViewSmokeContract(unittest.TestCase):
    def test_disposable_bridge_and_browser_share_business_clock(self):
        self.assertIn("new WebViewSelfCheckTimeProvider(TimeProvider.System)", HOST)
        self.assertIn("new BridgeSharedServices(fixture, clock)", HOST)
        self.assertIn("clock.GetBrowserInitializationScript()", HOST)
        self.assertIn("CheckActualWebViewNavigationAsync(core, fixture, clock, fixtureKey, recoveryOutput)", HOST)
        bridge = (ROOT / "src/AlTamam.DesktopHost/Bridge/Phase04BridgeRoundTripSelfCheck.cs").read_text(encoding="utf-8")
        self.assertIn("timeProvider.GetLocalNow().AddDays(-1)", bridge)

    def test_browser_fixture_clock_preserves_explicit_dates_and_elapsed_time(self):
        source = (ROOT / "src/AlTamam.DesktopHost/Bridge/WebViewSelfCheckTimeProvider.cs").read_text(encoding="utf-8")
        script = re.search(r'string script = """\s*([\s\S]*?)\s*""";', source).group(1)
        script = script.replace("__SELF_CHECK_UTC_MS__", "Date.parse('2026-11-07T12:00:00Z')")
        # Execute the actual injected script, not a parallel clock implementation.
        probe = """
const vm=require('node:vm'),assert=require('node:assert/strict');
let elapsed=10;
const context={performance:{now:()=>elapsed}};context.window=context;
vm.createContext(context);
vm.runInContext(SCRIPT,context);
assert.equal(vm.runInContext('new Date().toISOString()',context),'2026-11-07T12:00:00.000Z');
elapsed+=6000.25;
assert.equal(vm.runInContext('new Date().toISOString()',context),'2026-11-07T12:00:06.000Z');
assert.equal(vm.runInContext('Date.now()',context),Date.parse('2026-11-07T12:00:06Z'));
assert.equal(vm.runInContext('new Date(0).getTime()',context),0);
assert.equal(vm.runInContext('Date.UTC(2020,0,2)',context),Date.UTC(2020,0,2));
assert.equal(vm.runInContext('new Date(2020,0,2).getFullYear()',context),2020);
assert.equal(vm.runInContext('new Date() instanceof Date',context),true);
assert.equal(vm.runInContext('Number.isInteger(Date.now())',context),true);
assert.equal(vm.runInContext('class Derived extends Date {}; new Derived() instanceof Derived',context),true);
"""
        import json
        probe = probe.replace("SCRIPT", json.dumps(script))
        result = subprocess.run(["node", "-"], input=probe.encode("utf-8"), capture_output=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", errors="replace"))

    def test_recovery_form_submission_waits_for_actual_handler_registration(self):
        source = (ROOT / "src/AlTamam.DesktopHost/Bridge/Phase04RecoveryUiSelfCheck.cs").read_text(encoding="utf-8")
        ui = (ROOT / "src/AlTamam.WebUI/assets/js/functional-screens.js").read_text(encoding="utf-8")
        self.assertIn('form?.dataset?.authReady==="true"', source)
        self.assertIn('form.dataset.authReady="true"', ui)
        self.assertLess(ui.index('form.addEventListener("submit",async event=>'),
                        ui.index('form.dataset.authReady="true"'))
        # The UI itself must suppress premature native HTML form submission,
        # not merely the automated probe: a human can also press Enter early.
        gate = 'form.addEventListener("submit",event=>{'
        self.assertIn(gate, ui)
        self.assertIn('if(form.dataset.authReady!=="true")event.preventDefault();', ui)
        self.assertLess(ui.index(gate),
                        ui.index('const provisioning=parseData(await bridge().invoke("GetProvisioningState",{}))'))

    def test_recovery_probe_waits_for_late_shipped_bridge_after_root_reload(self):
        source = (ROOT / "src/AlTamam.DesktopHost/Bridge/Phase04RecoveryUiSelfCheck.cs").read_text(encoding="utf-8")
        script = re.search(r'string javascript = """\s*([\s\S]*?)\s*""";', source).group(1)
        import json
        # The C# host supplies these values at runtime: Node must run the same
        # post-substitution script, not throw on undefined test placeholders.
        script = script.replace("__OWNER__", json.dumps("Example123"))
        script = script.replace("__OUTPUT__", json.dumps("C:/test-recovery"))
        probe = """
const vm=require('node:vm'),assert=require('node:assert/strict');
const timers=[],sidebar={hidden:true},logout={hidden:true,isConnected:true};
const auth={dataset:{authReady:'false'},querySelector:()=>({value:''}),requestSubmit(){sidebar.hidden=false;logout.hidden=false;}};
const root={querySelector:sel=>sel==='.auth-form'&&sidebar.hidden?auth:
  sel==='.daily-roster-panel'&&!sidebar.hidden?{}:null,querySelectorAll:()=>[]};
let calls=0;
const lateBridge={hasBridge:()=>true,invoke:async()=>{calls++;throw Error('readiness-test-stop');}};
const context={document:{addEventListener(){},removeEventListener(){},
    getElementById:id=>id==='appViewport'?root:id==='logoutAction'?logout:null,
    querySelector:sel=>sel==='.app-sidebar'?sidebar:null},
  location:{hash:'#dashboard'},performance:{now:()=>Date.now()},
  setTimeout:fn=>{timers.push(fn);return timers.length;},console};
context.window=context;if(EARLY_BRIDGE)context.AltamamBridge=lateBridge;
vm.createContext(context);vm.runInContext(SCRIPT,context);
(async()=>{
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(calls,0,'no native action before bridge readiness');
  context.AltamamBridge=lateBridge;
  context.AltamamFunctional={render(){}};
  context.AltamamAdminSettings={renderSettings(){}};
  context.AltamamDialog={confirm(){},detailsReview(){},alert(){}};
  auth.dataset.authReady='true'; // native provisioning initialization finished
  for(let i=0;i<10;i++){
    timers.splice(0).forEach(fn=>fn());
    await new Promise(resolve=>setImmediate(resolve));
  }
  assert.equal(calls,1,'late native bridge must be resolved after modules arrive');
  assert.match(context.__r01RecoveryUi.reason,/readiness-test-stop/,
    'probe must reach the real invoke boundary rather than fail on an undefined reference');
})().catch(e=>{console.error(e);process.exitCode=1;});
""".replace("SCRIPT", json.dumps(script))
        for early in ["true", "false"]:
            with self.subTest(bridge_available_before_modules=early):
                code = probe.replace("EARLY_BRIDGE", early)
                result = subprocess.run(["node", "-"], input=code.encode("utf-8"), capture_output=True, timeout=20)
                self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", errors="replace"))

    def test_recovery_login_uses_real_ui_session_transition_not_extra_native_logout(self):
        # Run the ACTUAL embedded browser script with dashboard, auto-opened
        # settings and login states, including auto-settings after authentication.
        # This JS-only contract is not a substitute for real Windows SQLCipher E2E.
        import json
        source = (ROOT / "src/AlTamam.DesktopHost/Bridge/Phase04RecoveryUiSelfCheck.cs").read_text(encoding="utf-8")
        script = re.search(r'string javascript = """\s*([\s\S]*?)\s*""";', source).group(1)
        script = script.replace("__OWNER__", json.dumps("Example123"))
        script = script.replace("__OUTPUT__", json.dumps("C:/test-recovery"))
        code = r"""
const vm=require('node:vm'),assert=require('node:assert/strict');
async function check(initialMode){
  let mode=initialMode, logoutClicks=0, submits=0;
  const sidebarWasInitiallyVisible=initialMode==='dashboard'||initialMode==='settings';
  const calls=[],sidebar={hidden:mode==='login'||mode==='login-auto-settings'};
  const auth={dataset:{authReady:'false'},querySelector:selector=>({value:''}),requestSubmit(){
    submits++;mode=initialMode==='login-auto-settings'?'settings':'dashboard';
    sidebar.hidden=false;logoutAction.hidden=false;
  }};
  const viewport={querySelector(selector){
    if(selector==='.daily-roster-panel,.auth-form')return {};
    if(selector==='.daily-roster-panel')return mode==='dashboard'?{}:null;
    if(selector==='.settings-heading')return mode==='settings'?{}:null;
    if(selector==='.auth-form')return sidebar.hidden?auth:null;
    return null;
  },querySelectorAll(){return []}};
  const logoutAction={hidden:sidebar.hidden,isConnected:true,click(){
    logoutClicks++;mode='login';sidebar.hidden=true;logoutAction.hidden=true;
  }};
  const document={addEventListener(){},removeEventListener(){},
    getElementById(id){return id==='appViewport'?viewport:
      id==='logoutAction'?logoutAction:null},
    querySelector(selector){return selector==='.app-sidebar'?sidebar:null}};
  const bridge={hasBridge:()=>true,invoke:async(action)=>{
    calls.push(action);throw Error('native-boundary-stop');
  },setSessionNonce(){}};
  // Fast deterministic scheduling exposes a 60-second UI readiness bug in milliseconds.
  const context={document,location:{hash:'#dashboard'},console,
    performance:{now:()=>Date.now()},setTimeout:callback=>setImmediate(callback)};
  context.window=context;context.AltamamBridge=bridge;
  context.AltamamFunctional={render(){}};
  context.AltamamAdminSettings={renderSettings(){}};
  context.AltamamDialog={confirm(){},detailsReview(){},alert(){}};
  vm.createContext(context);vm.runInContext(SCRIPT_JSON,context);
  for(let i=0;i<6;i++)await new Promise(resolve=>setImmediate(resolve));
  assert.equal(submits,0,'must not submit form before real handler is ready');
  auth.dataset.authReady='true';
  for(let i=0;i<3000&&!context.__r01RecoveryUi?.reason;i++)
    await new Promise(resolve=>setImmediate(resolve));
  assert.equal(logoutClicks,sidebarWasInitiallyVisible?1:0,
    'logout must use the actual shipped UI when a prior session exists');
  assert.equal(submits,1,'login form must submit through the real UI');
  assert.deepEqual(calls,['GetCurrentSession'],
    'no redundant native Logout/provisioning requests during auth transition');
  assert.match(context.__r01RecoveryUi.reason,/native-boundary-stop/);
}
(async()=>{
  await check('dashboard');
  await check('settings'); // actual default-drive-unavailable auto-navigation
  await check('login');
  await check('login-auto-settings');
})().catch(e=>{
  console.error(e);process.exitCode=1;
});
""".replace("SCRIPT_JSON", json.dumps(script))
        result = subprocess.run(["node", "-"], input=code.encode("utf-8"),
                                capture_output=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", errors="replace"))

    def test_recovery_ui_uses_real_gateway_and_preserves_independent_rescue_check(self):
        source = (ROOT / "src/AlTamam.DesktopHost/Bridge/Phase04RecoveryUiSelfCheck.cs").read_text(encoding="utf-8")
        match = re.search(r'string javascript = """\s*([\s\S]*?)\s*""";', source)
        self.assertIsNotNone(match)
        syntax = subprocess.run(["node", "--check", "-"], input=match.group(1).encode("utf-8"), capture_output=True, timeout=20)
        self.assertEqual(syntax.returncode, 0, syntax.stderr.decode("utf-8", errors="replace"))
        self.assertIn("Phase04RecoveryUiSelfCheck.RunAsync", HOST)
        self.assertIn('rescue-before-reset-*.dbbackup', source)
        self.assertIn('copy.HasValidIntegrity()', source)
        self.assertIn('Count(copy, "Persons") != people', source)
        self.assertIn('wrong-reset-password', source)
        self.assertIn('RestoreDatabaseAtomically', (ROOT / "src/AlTamam.Application/Bridge/BridgeHandlerCatalog.cs").read_text(encoding="utf-8"))

    def test_transfer_uses_preexisting_person_with_yesterday_assignment(self):
        # A person created through the live first-use UI *today* has an assignment
        # starting today, which SQLCipher must not close with a zero-day interval.
        # Use a separate historical fixture without relaxing storage integrity.
        bridge = (ROOT / "src/AlTamam.DesktopHost/Bridge/Phase04BridgeRoundTripSelfCheck.cs").read_text(
            encoding="utf-8")
        self.assertIn('AddDays(-1).ToString("yyyy-MM-dd"', bridge)
        self.assertIn("TST-PHASE15-MOVABLE", bridge)
        self.assertIn("INSERT INTO PersonAssignments", bridge)
        self.assertIn("INSERT INTO PersonIdentityHistory", bridge)
        self.assertIn("personIds:[movableId]", bridge)
        self.assertNotIn("personIds:[personId]", bridge)
        self.assertIn("transferMovement.Parameters.AddWithValue(\"$person\",movableId)", bridge)
        self.assertIn("transferredAssignment.Parameters.AddWithValue(\"$person\",movableId)", bridge)
        self.assertIn("transferAudit.Parameters.AddWithValue(\"$person\",movableId)", bridge)

    def test_recovery_injection_acknowledges_and_state_survives_unstable_dom(self):
        # Test the actual embedded JavaScript, not a duplicate implementation.
        # Baseline RED: the previous live uiState getter throws during DOM
        # navigation and hides every native stage when JSON.stringify executes.
        import json
        source = (ROOT / "src/AlTamam.DesktopHost/Bridge/Phase04RecoveryUiSelfCheck.cs").read_text(encoding="utf-8")
        script = re.search(r'string javascript = """\s*([\s\S]*?)\s*""";', source).group(1)
        probe = r"""
const vm=require('node:vm'),assert=require('node:assert/strict');
const ctx={
  document:{
    readyState:'loading',
    addEventListener(){},removeEventListener(){},
    getElementById(){return null},
    querySelector(){throw new Error('DOM navigation in progress')}
  },
  location:{hash:'#dashboard'},
  performance:{now:()=>0},
  setTimeout(){},
  console
};
ctx.window=ctx;vm.createContext(ctx);
const receipt=vm.runInContext(SCRIPT,ctx);
assert.equal(receipt,'R01_RECOVERY_INJECTED',
  'native host must verify execution, not treat WebView2 null as success');
assert.equal(ctx.__r01RecoveryUi.status,'running');
assert.equal(ctx.__r01RecoveryUi.stage,'modules-ready');
assert.doesNotThrow(()=>JSON.stringify(ctx.__r01RecoveryUi),
  'status polling must never synchronously access a moving DOM');
""".replace("SCRIPT", json.dumps(script))
        result = subprocess.run(["node", "-"], input=probe.encode("utf-8"),
                                capture_output=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stderr.decode("utf-8", errors="replace"))
        self.assertIn("Recovery UI: browser probe startup acknowledged", source)
        # Verify native C# receipt comparison is compilable without an escaped-quote slip.
        self.assertIn('if (acknowledged != JsonSerializer.Serialize("R01_RECOVERY_INJECTED"))', source)

    def test_recovery_native_probe_records_actual_navigation_and_start_boundaries(self):
        # A timed-out Windows process was last observed to have passed the actual
        # bridge but showed no recovery stage at all. Until these boundaries are
        # recorded we cannot distinguish native DB blocking from WebView2 reload.
        source = (ROOT / "src/AlTamam.DesktopHost/Bridge/Phase04RecoveryUiSelfCheck.cs").read_text(encoding="utf-8")
        required = (
            "Recovery UI: entered native fixture",
            "Recovery UI: native person/user census complete",
            "Recovery UI: navigation requested",
            "Recovery UI: root navigation completed",
            "Recovery UI: browser probe injection completed",
        )
        for marker in required:
            with self.subTest(marker=marker):
                self.assertIn(marker, source)
        self.assertIn("Recovery UI state:", source)
        # Provenance is never inferred from a partial trace or prewritten flags.
        runner = (ROOT / "build/test-webview-ui-e2e.ps1").read_text(encoding="utf-8")
        self.assertIn("PASS R01 real backup restore-preview reset UI and independent encrypted rescue", runner)
        self.assertIn('if ($process.ExitCode -ne 0)', runner)

    def test_prepackage_recovery_is_a_separate_bounded_real_windows_process(self):
        # Do not let earlier modal/69-route work consume the recovery budget.
        # This is a harness contract; both actual Windows processes must pass CI.
        runner = (ROOT / "build/test-webview-ui-e2e.ps1").read_text(encoding="utf-8")
        self.assertIn('"--webview-split-recovery-e2e"', runner)
        self.assertIn('"--webview-split-route-e2e"', runner)
        self.assertIn('$requiredPhases = @(', runner)
        self.assertIn('PASS R01 real backup restore-preview reset UI and independent encrypted rescue', runner)
        self.assertIn('WaitForExit(240000)', runner)
        self.assertIn('if ($process.ExitCode -ne 0)', runner)
        self.assertNotIn('continue-on-error', runner)
        host = (ROOT / "src/AlTamam.DesktopHost/MainWindow.xaml.cs").read_text(encoding="utf-8")
        self.assertIn('"--webview-split-recovery-e2e"', host)
        self.assertIn('if (!splitRecovery && syntheticDatabase is not null', host)
        self.assertIn('Phase04RecoveryUiSelfCheck.RunAsync', host)

    def test_recovery_session_wait_reports_bounded_real_dom_and_bridge_diagnostics(self):
        source = (ROOT / "src/AlTamam.DesktopHost/Bridge/Phase04RecoveryUiSelfCheck.cs").read_text(encoding="utf-8")
        self.assertIn("performance.now()", source)
        self.assertIn('sidebarHidden=', source)
        self.assertIn('logoutHidden=', source)
        self.assertIn('documentState=', source)
        self.assertIn('bridgeHasTransport=', source)
        self.assertIn('Recovery UI state:', source)
        self.assertIn('recovery UI did not reach ', source)

    def test_real_webview_bridge_inline_javascript_must_parse_before_packaging(self):
        # C# accepts raw JS strings without parsing them: duplicate const declarations
        # make ExecuteScriptAsync return null at runtime. Fail CI before compilation.
        bridge = (ROOT / "src/AlTamam.DesktopHost/Bridge/Phase04BridgeRoundTripSelfCheck.cs").read_text(
            encoding="utf-8")
        match = re.search(r'string javascript = """\s*([\s\S]*?)\s*""";', bridge)
        self.assertIsNotNone(match, "real WebView2 bridge JavaScript is missing")
        # Pass bytes, not locale-encoded text: Windows' cp1252 cannot represent
        # the Arabic UI fixture. Node reads the UTF-8 byte stream unchanged.
        syntax = subprocess.run(["node", "--check", "-"],
                                input=match.group(1).encode("utf-8"),
                                capture_output=True, timeout=20, check=False)
        self.assertEqual(syntax.returncode, 0,
                         "Invalid embedded WebView2 JS (preflight): " +
                         syntax.stderr.decode("utf-8", errors="replace"))

    def test_embedded_js_parser_must_not_use_locale_dependent_windows_encoding(self):
        # On GitHub Windows Python chooses cp1252 for subprocess text pipes.
        # The Arabic browser fixture is not encodable as cp1252; force UTF-8.
        import inspect
        code = inspect.getsource(
            self.test_real_webview_bridge_inline_javascript_must_parse_before_packaging)
        self.assertTrue(
            'input=match.group(1).encode("utf-8")' in code
            or 'encoding="utf-8"' in code,
            "The Node stdin pipe must explicitly use UTF-8; cp1252 fails on Arabic")
        self.assertIn("وجهة نقل اختبارية", (
            ROOT / "src/AlTamam.DesktopHost/Bridge/Phase04BridgeRoundTripSelfCheck.cs"
        ).read_text(encoding="utf-8"))

    def test_selfcheck_uses_isolated_temporary_profile_without_database(self):
        self.assertIn('webViewSelfCheck\n                ? Path.Combine(Path.GetTempPath()', HOST)
        self.assertIn('"AlTamam-WebView2-SelfCheck-" + Guid.NewGuid()', HOST)
        self.assertIn('Path.Combine(AppDataLayout.DataRoot, "WebView2")', HOST)
        self.assertIn('Task<PreparedServices>? preparing = webViewSelfCheck\n            ? null', HOST)

    def test_actual_navigation_and_document_title_remain_required(self):
        self.assertIn("await CheckActualWebViewNavigationAsync(core)", HOST)
        self.assertIn('core.Navigate(LocalWebUiOrigin.EntryPointUrl)', HOST)
        self.assertIn("if (!result.IsSuccess)", HOST)
        self.assertIn('title.Contains("التمام اليومي", StringComparison.Ordinal)', HOST)
        self.assertIn('Shutdown(0)', HOST)

    def test_diagnostics_expose_early_start_and_webview_stage_without_secrets(self):
        # App.MainWindow is the WPF Window PROPERTY; use the fully qualified
        # C# type, otherwise CS1061 breaks both Windows workflows.
        self.assertIn('AlTamam.DesktopHost.MainWindow.TraceWebViewSelfCheck("WPF application startup")', APP)
        self.assertNotIn('        MainWindow.TraceWebViewSelfCheck(', APP)
        self.assertIn('TraceWebViewSelfCheck("Creating CoreWebView2Environment")', HOST)
        self.assertIn('TraceWebViewSelfCheck("Ensuring CoreWebView2 control")', HOST)
        self.assertIn('TraceWebViewSelfCheck("Navigating to bundled offline WebUI")', HOST)
        self.assertIn("ALTAMAM_WEBVIEW_SELFCHECK_TRACE", HOST)

    def test_runner_retries_at_most_twice_and_does_not_allow_false_success(self):
        self.assertIn("for ($attempt = 1; $attempt -le 2; $attempt++)", RELEASE)
        self.assertIn("$probe.WaitForExit(60000)", RELEASE)
        self.assertIn("$probe.Kill($true)", RELEASE)
        self.assertIn("if ($exited -and $probe.ExitCode -eq 0)", RELEASE)
        self.assertIn("if (-not $passed) { throw", RELEASE)
        self.assertIn("WEBVIEW-SELFCHECK PASS (real Windows WebView2 navigation)", RELEASE)
        self.assertNotIn("continue-on-error: true", RELEASE)

    def test_packaged_portable_and_installer_webview_navigation_is_bounded_and_diagnostic(self):
        package = (ROOT / "build/test-package-appdata-preservation.ps1").read_text(
            encoding="utf-8")
        # A slow cold start in the actual x86 ZIP must not make the package
        # acceptance path weaker or less observable than the pre-package probe.
        self.assertIn("function Run-PackagedWebViewChecked", package)
        self.assertIn("for ($attempt = 1; $attempt -le 2; $attempt++)", package)
        self.assertIn('Run-Checked $file @("--webview-selfcheck") 90', package)
        self.assertIn("ALTAMAM_WEBVIEW_SELFCHECK_TRACE", package)
        self.assertIn('Assert-True $passed "WebView2 navigation failed', package)
        self.assertIn('Run-PackagedWebViewChecked $portableExe "Portable ZIP"', package)
        self.assertIn('Run-PackagedWebViewChecked $installedExe "Installer"', package)
        self.assertIn('Run-PackagedWebViewChecked $installedExe "Reinstall"', package)
        # Layout and independent AppData integrity checks must not be removed.
        self.assertIn('Run-Checked $portableExe @("--layout-selfcheck") 90', package)
        self.assertIn('Assert-DataPreserved "Portable direct run"', package)
        self.assertIn('Assert-DataPreserved "Installer run"', package)
        self.assertIn('Assert-DataPreserved "Reinstall"', package)

    def test_phase14_real_packaged_sqlcipher_bridge_is_mandatory_on_x86_and_x64(self):
        package = (ROOT / "build/test-package-appdata-preservation.ps1").read_text(encoding="utf-8")
        # Different bounded processes, same customer-facing packaged binary:
        # bridge/SQLCipher/UI must pass AND all 69 routes must really navigate.
        # A single 180s process previously timed out on win-x86 after the bridge
        # passed, without ever finishing the 69-route evidence gate.
        self.assertIn('$bridgeTimeoutSeconds = Get-PackagedBridgeTimeoutSeconds -Runtime $ExpectedArch', package)
        self.assertIn('Run-Checked $file @("--webview-selfcheck","--webview-ui-e2e","--webview-bridge-e2e","--webview-split-route-e2e","--webview-split-recovery-e2e") $bridgeTimeoutSeconds', package)
        watchdog = (ROOT / "build/package-process-watchdog.ps1").read_text(encoding="utf-8")
        self.assertIn('function Get-PackagedBridgeTimeoutSeconds', watchdog)
        self.assertIn('"win-x86" { return 480 }', watchdog)
        self.assertIn('"win-x64" { return 300 }', watchdog)
        self.assertIn('Run-Checked $file @("--webview-selfcheck","--webview-route-e2e") 120', package)
        self.assertIn('Assert-True ($routeEvidence.Contains("PASS all 69 manifest routes opened in real Windows WebView2"))', package)
        self.assertIn('Assert-True ($routeEvidence.Contains("PASS bundled offline WebUI title verified"))', package)
        self.assertIn('if ((syntheticDatabase is not null && !splitRoutes) || routesOnly)', HOST)
        self.assertIn('await Phase04ManifestRouteSelfCheck.RunAsync(', HOST)
        self.assertIn('Run-PackagedWebViewChecked $portableExe "Portable ZIP" $true', package)
        self.assertIn('Run-PackagedWebViewChecked $installedExe "Installer" $true', package)
        self.assertIn("PASS actual HTML-C#-SQLCipher-HTML roundtrip", package)
        self.assertIn("PASS real Windows GeneralAdmin Admin Supervisor", package)
        self.assertIn("PASS all 69 manifest routes opened", package)
        for runtime in ("win-x86", "win-x64"):
            self.assertIn("runtime: " + runtime, RELEASE)
        self.assertLess(RELEASE.index("needs: build-test"),
                        RELEASE.index("test-package-appdata-preservation.ps1"))

    def test_packaged_bridge_does_not_rerun_prepackage_recovery_after_long_x86_fixture(self):
        # Regression: x86 packaged Phase04 bridge reaches real recovery *again*
        # after a measured >5-minute SQLCipher/69-route workload. The unwanted
        # extra recovery session check timed out; pre-package Core and Release
        # already require recovery in their own isolated real Windows processes.
        package = (ROOT / "build/test-package-appdata-preservation.ps1").read_text(encoding="utf-8")
        run = (ROOT / "build/test-webview-ui-e2e.ps1").read_text(encoding="utf-8")
        host = (ROOT / "src/AlTamam.DesktopHost/MainWindow.xaml.cs").read_text(encoding="utf-8")
        import re
        match = re.search(r'Run-Checked \$file @\(([^)]*)\) \$bridgeTimeoutSeconds', package)
        self.assertIsNotNone(match, "packaged SQLCipher must use an actual bounded Windows process")
        args = re.findall(r'"([^"]+)"', match.group(1))
        self.assertEqual(args, [
            "--webview-selfcheck",
            "--webview-ui-e2e",
            "--webview-bridge-e2e",
            "--webview-split-route-e2e",
            "--webview-split-recovery-e2e"
        ], "the packaged process must not repeat the independently proven recovery suite")
        # Both x86/x64 Portable and Installer must still run the REAL bridge.
        for expected in ('Run-PackagedWebViewChecked $portableExe "Portable ZIP" $true',
                         'Run-PackagedWebViewChecked $installedExe "Installer" $true',
                         'Run-Checked $file @("--webview-selfcheck","--webview-route-e2e") 120'):
            self.assertIn(expected, package)
        self.assertIn("if (!splitRecovery && syntheticDatabase is not null", host)
        self.assertIn("await Phase04RecoveryUiSelfCheck.RunAsync", host)
        self.assertIn('Name = "recovery"', run)
        self.assertIn('Name = "modal-bridge-routes"', run)
        self.assertIn('PASS R01 real backup restore-preview reset UI and independent encrypted rescue', run)
        self.assertIn('if ($process.ExitCode -ne 0)', run)
        self.assertIn('Assert-True $passed "WebView2 navigation failed', package)
        for workflow in (CORE, RELEASE):
            self.assertIn('pwsh -NoProfile -File ./build/test-webview-ui-e2e.ps1', workflow)

    def test_every_windows_manifest_contract_exit_code_is_checked_immediately(self):
        for workflow in (CORE, RELEASE):
            self.assertIn(
                "python build/test-webview-selfcheck-contract.py\n"
                "          if ($LASTEXITCODE -ne 0) { throw 'Windows real WebView2 smoke-test contract failed' }\n"
                "          python build/test-local-reminders-contract.py\n"
                "          if ($LASTEXITCODE -ne 0) { throw 'Local reminders contract failed' }",
                workflow,
            )

    def test_contract_is_run_by_both_parallel_windows_workflows_before_build(self):
        for workflow in (CORE, RELEASE):
            self.assertIn("python build/test-webview-selfcheck-contract.py", workflow)
            self.assertIn("test-release-artifact-provenance.py", workflow)
            self.assertLess(workflow.index("python build/test-webview-selfcheck-contract.py"),
                            workflow.index("dotnet publish") if "dotnet publish" in workflow else len(workflow))

    def test_real_windows_modal_e2e_is_required_before_packaging(self):
        driver = (ROOT / "build/test-webview-ui-e2e.ps1").read_text(encoding="utf-8")
        self.assertIn('Contains("--webview-ui-e2e"', HOST)
        self.assertIn("await CheckActualWebViewModalInteractionsAsync(core)", HOST)
        for evidence in ('location.origin === "https://manger.adwas.example"',
                         "document.documentElement.dir", "dialog.form(",
                         "getBoundingClientRect()", "required field rejects empty input",
                         'key:"Escape"', "document.activeElement === opener"):
            self.assertIn(evidence, HOST)
        self.assertIn('TraceWebViewSelfCheck("UI-E2E PASS', HOST)
        self.assertIn('("--webview-selfcheck","--webview-ui-e2e","--webview-bridge-e2e","--webview-split-recovery-e2e")', driver)
        self.assertIn('("--webview-selfcheck","--webview-bridge-e2e","--webview-split-route-e2e","--webview-recovery-only-e2e")', driver)
        self.assertIn("PASS recovery-only native SQLCipher founder fixture", driver)
        self.assertIn("PrepareSyntheticRecoveryFixtureAsync", HOST)
        self.assertIn("PASS actual HTML-C#-SQLCipher-HTML roundtrip", driver)
        self.assertIn("PASS real Windows modal-confirmed internal transfer persisted exactly once in SQLCipher", driver)
        package = (ROOT / "build/test-package-appdata-preservation.ps1").read_text(encoding="utf-8")
        self.assertIn("PASS real Windows modal-confirmed internal transfer persisted exactly once in SQLCipher", package)
        bridge = (ROOT / "src/AlTamam.DesktopHost/Bridge/Phase04BridgeRoundTripSelfCheck.cs").read_text(encoding="utf-8")
        self.assertIn("PASS real Windows modal-confirmed internal transfer persisted exactly once in SQLCipher", bridge)
        self.assertIn("PreviewInternalTransfer", bridge)
        self.assertIn("CommitInternalTransfer", bridge)
        self.assertIn("transferReview", bridge)
        self.assertIn("QueryMovementArchive", bridge)
        # Do not lose the native operation that first failed behind a generic
        # user-facing ValidationError. Only public stage/errorCode are logged.
        self.assertIn('probeStage = "PreviewInternalTransfer"', bridge)
        self.assertIn('probeStage = "CommitInternalTransfer"', bridge)
        self.assertIn('probeStage = "QueryMovementArchive"', bridge)
        self.assertIn('reason:probeStage + ": " +', bridge)
        self.assertIn("await Phase04BridgeRoundTripSelfCheck.RunAsync", HOST)
        self.assertIn("uri.IsDefaultPort && uri.UserInfo.Length == 0", HOST)
        self.assertIn("WaitForExit(240000)", driver)
        self.assertIn("Phase04ManifestRouteSelfCheck.RunAsync", HOST)
        self.assertIn("PASS all 69 manifest routes opened", driver)
        self.assertIn("UI-E2E PASS real Windows", driver)
        self.assertIn("if ($process.ExitCode -ne 0)", driver)
        for workflow in (CORE, RELEASE):
            test_job = workflow.split("  core-tests:", 1)[-1] if workflow == CORE else workflow.split("  build-test:", 1)[-1].split("  package:", 1)[0]
            self.assertIn("pwsh -NoProfile -File ./build/test-webview-ui-e2e.ps1", test_job)
            self.assertIn("dotnet build AlTamam.slnx", test_job)
            self.assertLess(test_job.index("dotnet build AlTamam.slnx"),
                            test_job.index("pwsh -NoProfile -File ./build/test-webview-ui-e2e.ps1"))

    def test_recovery_ui_native_event_transport_and_actual_browser_probe(self):
        # The previous C# ExecuteScriptAsync state-poll hung on real Windows
        # during a browser login/navigation transition, after injection succeeded.
        # Keep the actual WebView2 + SQLCipher acceptance mandatory in both tracks.
        import json
        source = (ROOT / "src/AlTamam.DesktopHost/Bridge/Phase04RecoveryUiSelfCheck.cs").read_text(encoding="utf-8")
        script = re.search(r'string javascript = """\s*([\s\S]*?)\s*""";', source).group(1)
        self.assertIn("core.DocumentTitleChanged += onRecoveryTitleChanged", source)
        self.assertIn("core.DocumentTitleChanged -= onRecoveryTitleChanged", source)
        self.assertIn("await reported.WaitAsync(deadline.Token)", source)
        self.assertNotIn('"JSON.stringify(window.__r01RecoveryUi||null)"', source)
        for evidence in ("Count(database, \"Persons\") != 0", "copy.HasValidIntegrity()",
                         'Count(copy, "Persons") != people', 'Count(copy, "Users") != users',
                         'File.ReadAllText(Path.Combine(output, "invalid.dbbackup"))',
                         'PASS R01 real backup restore-preview reset UI and independent encrypted rescue'):
            self.assertIn(evidence, source)
        probe = r"""
const vm=require('node:vm'),assert=require('node:assert/strict');
let now=0;const callbacks=[],signals=[];
const doc={addEventListener(){},removeEventListener(){},
  getElementById(){return null;},querySelector(){return null;}};
Object.defineProperty(doc,'title',{set(value){
  assert.ok(value.startsWith('ALTAMAM_R01_RECOVERY:'));
  signals.push(JSON.parse(value.slice('ALTAMAM_R01_RECOVERY:'.length)));
}});
const context={document:doc,performance:{now:()=>now},
  location:{hash:'#dashboard'},setTimeout:fn=>{callbacks.push(fn)},console};
context.window=context;
vm.createContext(context);vm.runInContext(SCRIPT,context);
(async()=>{
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(signals[0]?.status,'running');
  assert.equal(signals[0]?.stage,'modules-ready');
  for(let i=0;i<9;i++){
    now+=10000;callbacks.splice(0).forEach(fn=>fn());
    await new Promise(resolve=>setImmediate(resolve));
  }
  const last=signals.at(-1);
  assert.equal(last.status,'failed','native must see explicit negative result');
  assert.match(last.reason,/shipped bridge and UI modules/);
  assert.ok(signals.length>=2,'intermediate updates survive without native JavaScript polling');
  assert.equal(signals.filter(x=>x.status==='pass').length,0);
})().catch(e=>{console.error(e);process.exitCode=1;});
""".replace("SCRIPT", json.dumps(script))
        check = subprocess.run(["node", "-"], input=probe.encode("utf-8"),
                               capture_output=True, timeout=20)
        self.assertEqual(check.returncode, 0, check.stderr.decode("utf-8", errors="replace"))


if __name__ == "__main__":
    unittest.main()
