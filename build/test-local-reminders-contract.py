#!/usr/bin/env python3
"""Independent, offline contract for optional force reminders (no SQLCipher writes)."""
from pathlib import Path
import re
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1]
HOST = ROOT / "src/AlTamam.DesktopHost"
UI = ROOT / "src/AlTamam.WebUI"


class ReminderContract(unittest.TestCase):
    def test_file_is_independent_from_sqlcipher_and_audit(self):
        source = (HOST / "Reminders/LocalReminderService.cs").read_text(encoding="utf-8")
        self.assertIn('Path.Combine(AppDataLayout.DataRoot, "Reminders", "reminders.json")', source)
        self.assertIn("File.Move(temp, _path, true)", source)
        self.assertNotIn("AlTamamDbContext", source)
        self.assertNotIn("AuditEvents", source)
        self.assertNotIn("AppSettings", source)
        self.assertIn("ResolveSessionAsync", (HOST / "Bridge/DesktopBridgeGateway.cs").read_text(encoding="utf-8"))
        self.assertIn("IsAllowedOrigin", source)

    def test_missed_silent_and_live_bell_with_five_minute_snooze(self):
        source = (HOST / "Reminders/LocalReminderService.cs").read_text(encoding="utf-8")
        self.assertIn("due < _startedLocal", source)
        self.assertIn("AddMinutes(5)", source)
        self.assertIn("SystemSounds.Exclamation.Play()", source)
        self.assertIn("Delivered = true", source)
        self.assertIn("Completed = true", source)

    def test_auth_and_role_visibility_without_new_main_section_or_route(self):
        admin = (UI / "assets/js/admin-settings.js").read_text(encoding="utf-8")
        app = (UI / "assets/js/altamam-app.js").read_text(encoding="utf-8")
        self.assertIn('["reminders","التنبيهات"]', admin)
        self.assertIn('screen==="reminders"', admin)
        self.assertNotIn('reminders:"', app)
        self.assertIn("GeneralAdministration", (HOST / "Reminders/LocalReminderService.cs").read_text(encoding="utf-8"))
        self.assertIn("OrdinaryAdministration", (HOST / "Reminders/LocalReminderService.cs").read_text(encoding="utf-8"))
        self.assertIn("Supervisor", (HOST / "Reminders/LocalReminderService.cs").read_text(encoding="utf-8"))
        self.assertIn("window.AltamamReminders?.start()", (UI / "assets/js/functional-screens.js").read_text(encoding="utf-8"))
        self.assertIn("window.AltamamReminders?.stop()", (UI / "assets/js/functional-screens.js").read_text(encoding="utf-8"))

    def test_unacknowledged_due_alerts_survive_page_navigation(self):
        source = (HOST / "Reminders/LocalReminderService.cs").read_text(encoding="utf-8")
        module = (UI / "assets/js/screen-module.js").read_text(encoding="utf-8")
        self.assertIn("item.Delivered || due < _startedLocal", source)
        self.assertIn('new URL("reminders.js"', module)
        self.assertIn("window.AltamamReminders.start()", module)
        self.assertIn("GetCurrentSession", module)

    def test_browser_module_is_syntax_checked_and_top_right(self):
        path = UI / "assets/js/reminders.js"
        js = path.read_text(encoding="utf-8")
        test = subprocess.run(["node", "--check", str(path)], capture_output=True,
                              timeout=20, check=False)
        self.assertEqual(test.returncode, 0, test.stderr.decode("utf-8", errors="replace"))
        self.assertIn("تأجيل 5 دقائق", js)
        self.assertIn("إيقاف", js)
        self.assertIn("reminder-toast", js)
        self.assertIn("reminderToasts", js)
        css = (UI / "assets/css/reminders.css").read_text(encoding="utf-8")
        self.assertIn("right: 18px", css)
        self.assertIn("position: fixed", css)
        index = (UI / "index.html").read_text(encoding="utf-8")
        self.assertIn("assets/js/reminders.js", index)
        self.assertIn("assets/css/reminders.css", index)


if __name__ == "__main__":
    unittest.main(verbosity=2)
