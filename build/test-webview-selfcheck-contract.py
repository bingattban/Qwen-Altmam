#!/usr/bin/env python3
"""Guard real x86/x64 WebView2 smoke tests against no-op or unbounded bypasses."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
HOST = (ROOT / "src/AlTamam.DesktopHost/MainWindow.xaml.cs").read_text(encoding="utf-8")
APP = (ROOT / "src/AlTamam.DesktopHost/App.xaml.cs").read_text(encoding="utf-8")
RELEASE = (ROOT / ".github/workflows/windows-release.yml").read_text(encoding="utf-8")
CORE = (ROOT / ".github/workflows/windows-build.yml").read_text(encoding="utf-8")


class WebViewSmokeContract(unittest.TestCase):
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
        self.assertIn('TraceWebViewSelfCheck("WPF application startup")', APP)
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

    def test_contract_is_run_by_both_parallel_windows_workflows_before_build(self):
        for workflow in (CORE, RELEASE):
            self.assertIn("python build/test-webview-selfcheck-contract.py", workflow)
            self.assertIn("test-release-artifact-provenance.py", workflow)
            self.assertLess(workflow.index("python build/test-webview-selfcheck-contract.py"),
                            workflow.index("dotnet publish") if "dotnet publish" in workflow else len(workflow))


if __name__ == "__main__":
    unittest.main()
