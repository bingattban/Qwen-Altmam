#!/usr/bin/env python3
"""SECTION 11.17: exact customer-facing x86/x64 filenames; synthetic fixtures only.

Run in BOTH independent Windows Core/Release preflights. It does not claim that
customer artifacts exist until the Windows release packages and hashes pass.
"""
import importlib.util
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BUILD = ROOT / "build"
SPEC = importlib.util.spec_from_file_location("release_names", BUILD / "release_names.py")
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("Section 11 final filename mapping is not installed")
MOD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MOD)


class OwnerReleaseNames(unittest.TestCase):
    def test_exact_four_customer_file_names(self):
        wanted = {
            ("Windows-32bit", "Portable"): "AlTamamDaily_1.2.3_Portable_win-x86.zip",
            ("Windows-32bit", "Installer"): "AlTamamDaily_1.2.3_Setup_win-x86.exe",
            ("Windows-64bit", "Portable"): "AlTamamDaily_1.2.3_Portable_win-x64.zip",
            ("Windows-64bit", "Installer"): "AlTamamDaily_1.2.3_Setup_win-x64.exe",
        }
        names = {MOD.owner_filename("1.2.3", arch, kind)
                 for (arch, kind) in wanted}
        self.assertEqual(names, set(wanted.values()))
        for (arch, kind), exact in wanted.items():
            with self.subTest(arch=arch, kind=kind):
                self.assertEqual(MOD.owner_filename("1.2.3", arch, kind), exact)

    def test_ci_version_preserved_without_malformed_paths(self):
        self.assertEqual(
            MOD.owner_filename("0.0.880-ci", "Windows-64bit", "Installer"),
            "AlTamamDaily_0.0.880-ci_Setup_win-x64.exe")
        for bad in ("", "../escape", "a/b", "v 1", ".hidden", "a\\b", "v@1"):
            with self.subTest(version=bad), self.assertRaises(ValueError):
                MOD.owner_filename(bad, "Windows-64bit", "Portable")
        for bad in ("Windows-16bit", "win-x86", ""):
            with self.subTest(architecture=bad), self.assertRaises(ValueError):
                MOD.owner_filename("1.2.3", bad, "Installer")
        with self.assertRaises(ValueError):
            MOD.owner_filename("1.2.3", "Windows-32bit", "MSI")

    def test_release_pipeline_uses_owner_names_from_zip_through_checksum(self):
        workflow = (ROOT / ".github/workflows/windows-release.yml").read_text(encoding="utf-8")
        for key in ("AlTamamDaily_", "_Portable_", "_Setup_",
                    "python build/verify-release-artifacts.py",
                    "python build/verify-phase15-handover.py",
                    "build/test-package-appdata-preservation.ps1"):
            self.assertIn(key, workflow)
        self.assertNotIn("dist/portable/AlTamam-Portable-", workflow)
        self.assertNotIn("dist/installer/AlTamam-Installer-", workflow)
        for script in ("make-metadata.py", "verify-release-artifacts.py",
                       "stage-phase14-distributions.py", "verify-phase15-handover.py"):
            source = (BUILD / script).read_text(encoding="utf-8")
            self.assertIn("owner_filename", source, script)


if __name__ == "__main__":
    unittest.main(verbosity=2)
