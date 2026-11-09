#!/usr/bin/env python3
"""Prevent unsafe async-void WPF Closed disposal that releases the
single-instance mutex before SQLCipher/backup schedulers have drained.
This contract supplements, NOT replaces, real xUnit and Windows E2E.
"""
import unittest
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
HOST=(ROOT/"src/AlTamam.DesktopHost/MainWindow.xaml.cs").read_text(encoding="utf-8")
APP=(ROOT/"src/AlTamam.Application/Bridge/NativeHostShutdownGate.cs").read_text(encoding="utf-8")

class NativeShutdownLifecycle(unittest.TestCase):
    def test_wpf_cancels_close_synchronously_until_native_shutdown_finishes(self):
        self.assertIn("Closing += OnWindowClosing;", HOST)
        self.assertIn("e.Cancel = true;", HOST)
        self.assertIn("NativeHostShutdownGate", HOST)
        self.assertIn("_shutdownGate.IsCompleted", HOST)
        self.assertIn("_shutdownGate.StopAndDrainAsync", HOST)
        self.assertIn("StopForNativeRestoreAsync", HOST)
        self.assertIn("await _gateway.StopReceivingAndDrainAsync()", HOST)
        self.assertIn("await scheduled.DisposeAsync()", HOST)
        self.assertIn("_dbContext?.Dispose()", HOST)
        self.assertNotIn("Closed += async", HOST)

    def test_no_early_mutex_release_or_unsafe_gate_success(self):
        self.assertIn("await nativeShutdown().ConfigureAwait(false)", APP)
        self.assertIn("lock (_sync) _completed = true", APP)
        self.assertIn("IsCompleted", APP)
        self.assertIn("StopAndDrainAsync", APP)
        self.assertIn("await", APP)
        self.assertIn("Close();", HOST)
        self.assertIn("if ((_restoreInProgress && !_restoreMayClose) || _nativeRecoveryExportInProgress)", HOST)
        self.assertIn("--webview-selfcheck", HOST)
        self.assertIn("CancellationToken.None", HOST)

if __name__=="__main__":
    unittest.main(verbosity=2)
