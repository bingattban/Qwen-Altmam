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
        self.assertIn("CheckActualWebViewNavigationAsync(core, fixture, clock)", HOST)
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
        self.assertIn('Run-Checked $file @("--webview-selfcheck","--webview-ui-e2e","--webview-bridge-e2e","--webview-split-route-e2e") $bridgeTimeoutSeconds', package)
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
        self.assertIn('("--webview-selfcheck","--webview-ui-e2e","--webview-bridge-e2e")', driver)
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


if __name__ == "__main__":
    unittest.main()
