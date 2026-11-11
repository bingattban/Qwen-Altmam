#!/usr/bin/env python3
"""Static support-document contract only; NEVER substitute for SQLCipher,
Windows WPF/WebView2 integration, or independent physical customer UAT.
Protect operator docs against accidentally saying trusted native restore
is unavailable just because the untrusted JS/bridge action is disabled.
"""
import unittest
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
GUIDE=ROOT/"docs/support/CURRENT_RECOVERY_OPERATOR_GUIDE.md"
SUPPORT=[
    ROOT/"docs/support/PORTABLE_RECOVERY_SECURITY.md",
    ROOT/"docs/support/BACKUP_RECOVERY_MIGRATION_GUIDE.md",
    ROOT/"docs/support/OFFLINE_RESTORE_ACTIVATION_CORE.md",
    ROOT/"docs/support/NATIVE_RESTORE_REAUTHENTICATION_GATE.md",
    ROOT/"docs/support/SECTION11_CLEAN_OFFLINE_WINDOWS_SMOKE.md",
    ROOT/"docs/release/GO_NO_GO.md",
]
XAML=ROOT/"src/AlTamam.DesktopHost/MainWindow.xaml"
BRIDGE=ROOT/"src/AlTamam.Application/Bridge/BridgeHandlerCatalog.cs"

class RecoveryOperatorGuidanceContract(unittest.TestCase):
    def test_real_windows_menu_and_disabled_browser_action_are_both_reported(self):
        xaml=XAML.read_text(encoding="utf-8")
        bridge=BRIDGE.read_text(encoding="utf-8")
        guide=GUIDE.read_text(encoding="utf-8")
        self.assertIn('Click="OnNativeRestoreMenuClick"',xaml)
        self.assertIn('Click="OnNativeRecoveryKeyExportClick"',xaml)
        self.assertIn('"RestoreDatabaseAtomically" =>',bridge)
        self.assertIn("استخدم قائمة «استعادة نسخة احتياطية بأمان»",bridge)
        for phrase in (
            "استعادة نسخة احتياطية بأمان",
            "تصدير مفتاح الاسترداد المشفر",
            "RestoreDatabaseAtomically",
            "WebView2",
            "GeneralAdministration",
            ".dbbackup",
            ".altrecovery",
            "NO-GO",
            "NOT EXECUTED",
        ):
            with self.subTest(phrase=phrase):
                self.assertIn(phrase,guide)

    def test_all_active_operator_documents_point_to_the_live_security_boundary(self):
        for path in SUPPORT:
            with self.subTest(path=str(path.relative_to(ROOT))):
                content=path.read_text(encoding="utf-8")
                self.assertIn("CURRENT_RECOVERY_OPERATOR_GUIDE.md",content)
                self.assertIn("2026-11-09",content)
                self.assertIn("NO-GO",content)

    def test_no_unsupported_cross_device_import_or_manual_acceptance_claim(self):
        guide=GUIDE.read_text(encoding="utf-8")
        self.assertIn("no pre-login key-import UI",guide)
        self.assertIn("second Windows machine/account",guide)
        self.assertIn("power interruption",guide)
        self.assertIn("disk-full",guide)
        self.assertIn("x86",guide)
        self.assertIn("x64",guide)
        self.assertIn("not production-ready",guide)
        self.assertIn("never delete",guide)
        self.assertIn("owner signoff",guide)

if __name__=="__main__":
    unittest.main(verbosity=2)
