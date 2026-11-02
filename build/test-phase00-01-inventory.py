#!/usr/bin/env python3
"""Test-first, read-only Phase 00/01 inventory guard (NOT a UI/UAT certificate)."""
import csv
import json
import re
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
UI = ROOT / "src/AlTamam.WebUI"


class Phase00Inventory(unittest.TestCase):
    def test_matrix_matches_every_canonical_route_including_owner_and_html(self):
        manifest = json.loads((UI / "screen-manifest.json").read_text(encoding="utf-8"))
        with (ROOT / "docs/requirements/SCREEN_MATRIX.csv").open(
            encoding="utf-8-sig", newline=""
        ) as file:
            rows = list(csv.DictReader(file))
        screens = manifest["screens"]
        self.assertEqual(len(rows), len(screens), "inventory matrix must not omit any route")
        self.assertEqual(len(rows), manifest["screenCount"])
        self.assertEqual(len({r["screenId"] for r in rows}), len(rows))
        self.assertEqual(len({r["routeKey"] for r in rows}), len(rows))
        by_id = {r["screenId"]: r for r in rows}
        self.assertNotIn("screen_020", by_id)
        for screen in screens:
            row = by_id[screen["screenId"]]
            for csv_key, manifest_key in (
                ("routeKey", "routeKey"),
                ("currentManifestTitleAr", "titleAr"),
                ("currentKind", "kind"),
                ("owningPhase", "implementationPhase"),
                ("localAssetPath", "localAssetPath"),
            ):
                self.assertEqual(row[csv_key], screen[manifest_key],
                                 f"{screen['screenId']} {csv_key} diverged")
            self.assertTrue(row["approvedTitleAr"].strip())
            self.assertTrue(row["requiredRealBehavior"].strip())
            self.assertTrue(row["phase00State"].startswith("INVENTORIED_ONLY"))
            self.assertTrue((UI / row["localAssetPath"]).is_file())
        self.assertEqual(by_id["screen_025"]["routeKey"], "PersonnelReport")
        self.assertEqual(by_id["screen_070"]["routeKey"], "AddPerson")
        self.assertEqual(by_id["screen_002"]["routeKey"], "UnpreparedPersonnel")
        self.assertEqual(by_id["screen_069"]["routeKey"], "EightPmScheduler")
        self.assertEqual(by_id["screen_069"]["owningPhase"], "07")

    def test_historical_risk_register_is_clearly_labeled(self):
        risk = (ROOT / "docs/implementation/RISK_REGISTER.md").read_text(
            encoding="utf-8")
        header = risk[:600]
        self.assertIn("Historical snapshot (2026-10-23)", header)
        self.assertIn("PHASE00_01_02_LIVE_RECONCILIATION_2026-11-02.md", header)
        self.assertIn("| R-01 |", risk, "retain original historical risk evidence")

    def test_owner_overrides_and_original_phase_ownership_are_declared(self):
        adr = (ROOT / "docs/architecture/ADR-0001.md").read_text(encoding="utf-8")
        trace = (ROOT / "docs/requirements/REQUIREMENTS_TRACEABILITY.md").read_text(
            encoding="utf-8")
        owner = (ROOT / "docs/requirements/OWNER_DECISIONS_2026-10-31.md").read_text(
            encoding="utf-8")
        for value in ("screen_020", "screen_025", "screen_070", "08:00", "20:00",
                      "16:00", "PersonId", "SQLCipher", "DPAPI"):
            self.assertIn(value, adr + trace)
        self.assertIn("ثلاث عمليات مستقلة", owner)
        self.assertIn("الأيام السبعة", owner)
        for number in range(16):
            self.assertRegex(trace, rf"(?m)^\| {number:02d} \|")


class Phase01Foundation(unittest.TestCase):
    def test_real_layered_solution_and_independent_three_test_projects(self):
        doc = ET.parse(ROOT / "AlTamam.slnx")
        paths = [p.attrib["Path"] for p in doc.iter("Project")]
        production = [p for p in paths if p.startswith("src/")]
        tests = [p for p in paths if p.startswith("tests/")]
        self.assertEqual(len(production), 5)
        self.assertEqual(len(tests), 3)
        for path in paths:
            self.assertTrue((ROOT / path).is_file(), path)
        self.assertEqual(
            {p.split("/")[1] for p in tests},
            {"AlTamam.Tests", "AlTamam.UnitTests", "AlTamam.IntegrationTests"})
        domain = (ROOT / "src/AlTamam.Domain/AlTamam.Domain.csproj").read_text(
            encoding="utf-8")
        application = (ROOT / "src/AlTamam.Application/AlTamam.Application.csproj").read_text(
            encoding="utf-8")
        self.assertNotIn("WebView2", application)
        self.assertNotIn("AlTamam.DesktopHost", application)
        self.assertNotIn("AlTamam.DesktopHost", domain)
        self.assertNotIn("AlTamam.Infrastructure", domain)

    def test_two_workflows_are_independent_and_package_only_after_tests(self):
        workflows = ROOT / ".github/workflows"
        self.assertEqual({p.name for p in workflows.glob("*.yml")},
                         {"windows-ci.yml", "windows-release.yml"})
        core = (workflows / "windows-ci.yml").read_text(encoding="utf-8")
        release = (workflows / "windows-release.yml").read_text(encoding="utf-8")
        for content in (core, release):
            self.assertIn("pull_request:", content)
            self.assertIn("push:", content)
            self.assertIn("main", content)
            self.assertIn("test-phase00-01-inventory.py", content)
        self.assertIn("needs: build-test", release)
        self.assertIn("win-x86", release)
        self.assertIn("win-x64", release)
        self.assertIn("SHA256", release)

    def test_pinned_packages_and_sensitive_file_exclusions(self):
        packages = ET.parse(ROOT / "Directory.Packages.props")
        versions = [p.attrib["Version"] for p in packages.iter("PackageVersion")]
        self.assertGreaterEqual(len(versions), 8)
        self.assertTrue(all(re.fullmatch(r"[0-9]+(?:\.[0-9]+){1,3}", v)
                            for v in versions))
        ignored = (ROOT / ".gitignore").read_text(encoding="utf-8")
        for value in ("*.db", "*.key", "*.xlsx", "*.altrecovery", "*.csv",
                      "!docs/requirements/SCREEN_MATRIX.csv"):
            self.assertIn(value, ignored)


if __name__ == "__main__":
    unittest.main()
