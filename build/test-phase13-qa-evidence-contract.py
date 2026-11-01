#!/usr/bin/env python3
"""Phase 13 QA evidence safety contract: fail before lengthy Windows suites.
Only static structure is checked here; real Windows evidence still requires
the native WPF/WebView2 process and the exact GitHub workflow/run SHA."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
DRIVER = (ROOT / "build/test-webview-ui-e2e.ps1").read_text(encoding="utf-8")
PERF = (ROOT / "tooling/perf/Collect-Phase13WindowsFacts.ps1").read_text(encoding="utf-8")
VALIDATE = (ROOT / "tests/AlTamam.WindowsE2E/Validate-Phase13Evidence.ps1").read_text(encoding="utf-8")
CORE = (ROOT / ".github/workflows/windows-ci.yml").read_text(encoding="utf-8")
RELEASE = (ROOT / ".github/workflows/windows-release.yml").read_text(encoding="utf-8")


class Phase13EvidenceContract(unittest.TestCase):
    def test_sha_checks_never_depend_on_uninitialized_native_exitcode(self):
        for source in (DRIVER, PERF, VALIDATE):
            self.assertNotIn("$LASTEXITCODE", source)
            self.assertIn("$gitSha = & git -C", source)
            self.assertIn("$gitSucceeded = $?", source)
            self.assertIn("-not $gitSucceeded", source)
            self.assertIn("actualCheckoutSha = $checkoutSha", source
                          if source != VALIDATE else PERF)

    def test_exact_source_pr_head_is_exported_to_each_windows_evidence_step(self):
        for workflow in (CORE, RELEASE):
            self.assertIn("ALTAMAM_PHASE13_EVIDENCE_PATH:", workflow)
            self.assertIn("ALTAMAM_SOURCE_HEAD_SHA: ${{ github.event.pull_request.head.sha || github.sha }}", workflow)
            segment = workflow.split("- name: Phase 13 runner hardware", 1)[1].split("- name: Phase 13 reject", 1)[0]
            self.assertIn("ALTAMAM_SOURCE_HEAD_SHA:", segment)
            self.assertIn("Collect-Phase13WindowsFacts.ps1", segment)
            self.assertIn("Validate-Phase13Evidence.ps1", workflow)

    def test_real_search_measurement_precedes_measured_core_report(self):
        self.assertLess(CORE.index("- name: Capture measured ten-thousand-person search benchmark"),
                        CORE.index("- name: Phase 13 runner hardware"))
        self.assertIn("-MeasurementFile PerformanceEvidence/ten-thousand-person-search.txt", CORE)
        self.assertIn('if (-not (Test-Path -LiteralPath $MeasurementFile -PathType Leaf))', PERF)

    def test_driver_requires_native_three_role_evidence_and_fails_closed(self):
        self.assertIn('PASS real Windows GeneralAdmin Admin Supervisor SQLCipher bridge authorizations', DRIVER)
        self.assertIn('$process.ExitCode -ne 0', DRIVER)
        self.assertIn('Set-Content -LiteralPath $phase13', DRIVER)
        self.assertIn('realWpfWebView2ProcessExitedZero = $true', DRIVER)

if __name__ == "__main__":
    unittest.main()
