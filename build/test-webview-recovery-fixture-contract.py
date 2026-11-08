#!/usr/bin/env python3
"""Regression contract for the real Windows recovery E2E fixture.

The actual full WPF/WebView2/SQLCipher Windows process is mandatory; this
preflight checks that optimization does not skip roles, bridge, reset, or rescue.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RUNNER = (ROOT / "build/test-webview-ui-e2e.ps1").read_text(encoding="utf-8")
HOST = (ROOT / "src/AlTamam.DesktopHost/MainWindow.xaml.cs").read_text(encoding="utf-8")
RECOVERY = (ROOT / "src/AlTamam.DesktopHost/Bridge/Phase04RecoveryUiSelfCheck.cs").read_text(encoding="utf-8")

class WebViewRecoveryFixtureContract(unittest.TestCase):
    def test_both_real_processes_retain_full_coverage(self):
        self.assertIn('Name = "modal-bridge-routes"', RUNNER)
        self.assertIn('Name = "recovery"', RUNNER)
        self.assertIn("PASS all 69 manifest routes opened in real Windows WebView2", RUNNER)
        self.assertIn("PASS actual HTML-C#-SQLCipher-HTML roundtrip and forbidden requests", RUNNER)
        self.assertIn("PASS R01 real backup restore-preview reset UI and independent encrypted rescue", RUNNER)
        self.assertIn("--webview-recovery-only-e2e", RUNNER)
        self.assertIn("PASS recovery-only native SQLCipher founder fixture", RUNNER)
        self.assertIn("$process.WaitForExit(240000)", RUNNER)

    def test_recovery_uses_its_own_real_encrypted_fixture_not_doubled_probe(self):
        self.assertIn("--webview-recovery-only-e2e", HOST)
        self.assertIn("PrepareSyntheticRecoveryFixtureAsync", HOST)
        self.assertIn("Phase04BridgeRoundTripSelfCheck.RunAsync", HOST)
        self.assertIn("Phase04RecoveryUiSelfCheck.RunAsync", HOST)
        self.assertIn("PrepareSyntheticRecoveryFixtureAsync", RECOVERY)
        self.assertIn("ProvisionFirstAdministratorAsync", RECOVERY)
        self.assertIn("INSERT INTO Persons", RECOVERY)
        self.assertIn('Count(database, "Persons") != 0', RECOVERY)
        self.assertIn('Count(database, "Users") != 0', RECOVERY)
        self.assertIn("rescue-before-reset-*.dbbackup", RECOVERY)

if __name__ == "__main__":
    unittest.main(verbosity=2)
