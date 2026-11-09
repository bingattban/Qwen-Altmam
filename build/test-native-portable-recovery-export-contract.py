#!/usr/bin/env python3
"""Supplementary security boundary for native-only portable SQLCipher key export.
Actual Windows xUnit DPAPI/SQLCipher tests are independently required.
"""
import unittest
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
XAML=(ROOT/"src/AlTamam.DesktopHost/MainWindow.xaml").read_text(encoding="utf-8")
HOST=(ROOT/"src/AlTamam.DesktopHost/MainWindow.xaml.cs").read_text(encoding="utf-8")
DIALOG=ROOT/"src/AlTamam.DesktopHost/Bridge/NativeRecoveryKeyExportDialog.cs"
APP=ROOT/"src/AlTamam.Application/Bridge/NativePortableRecoveryExportService.cs"

class NativeRecoveryExportBoundary(unittest.TestCase):
    def test_physical_menu_and_password_boxes_only(self):
        self.assertIn('Click="OnNativeRecoveryKeyExportClick"', XAML)
        self.assertIn('OnNativeRecoveryKeyExportClick',HOST)
        dialog=DIALOG.read_text(encoding="utf-8")
        self.assertGreaterEqual(dialog.count("new PasswordBox"),3)
        self.assertIn("ShowDialog()",dialog)
        self.assertIn("RecoveryPassphrase",dialog)
        self.assertIn("SaveFileDialog",HOST)
        self.assertIn("CurrentGeneralAdministratorAsync",HOST)

    def test_no_browser_secret_and_no_close_during_export(self):
        self.assertIn("_nativeRecoveryExportInProgress", HOST)
        self.assertIn("if ((_restoreInProgress && !_restoreMayClose) || _nativeRecoveryExportInProgress)", HOST)
        self.assertIn("NativePortableRecoveryExportService",HOST)
        self.assertNotIn("PostWebMessageAsString",APP.read_text(encoding="utf-8"))
        self.assertIn("VerifyCurrentGeneralAdministratorPasswordAsync",APP.read_text(encoding="utf-8"))
        self.assertIn("ExportPortableRecoveryKey",APP.read_text(encoding="utf-8"))

if __name__=="__main__":
    unittest.main(verbosity=2)
