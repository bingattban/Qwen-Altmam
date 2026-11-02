#!/usr/bin/env python3
"""Evidence-backed regressions for authorized Phases 00, 01 and 02.

This is a CI **preflight** contract test. It cannot replace compiled Windows
SQLCipher tests, physical clean-machine acceptance, or the missing canonical
owner master archive. Those remain separately recorded in the reconciliation.
"""
import json
import re
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def contracts(root=ROOT, *, core_override=None, release_override=None,
              runner_override=None, db_override=None):
    errors = []
    def need(condition, message):
        if not condition:
            errors.append(message)

    screens = json.loads((root / "src/AlTamam.WebUI/screen-manifest.json").read_text(encoding="utf-8"))
    actions = json.loads((root / "src/AlTamam.WebUI/action-manifest.json").read_text(encoding="utf-8"))
    routes = screens["screens"]
    native_actions = actions["actions"]
    need(screens["screenCount"] == len(routes) == 69, "Phase00: 69 canonical routes drifted")
    need(len({x["routeKey"] for x in routes}) == len(routes), "Phase00: duplicate route key")
    need(all((root / "src/AlTamam.WebUI" / x["localAssetPath"]).is_file()
             for x in routes), "Phase00: registered screen asset missing")
    need(len(native_actions) == 103 and
         len({x["action"] for x in native_actions}) == 103,
         "Phase00: canonical 103 actions drifted")

    solution = ET.parse(root / "AlTamam.slnx").getroot()
    declared = [e.attrib["Path"] for e in solution.iter("Project")]
    need(len(declared) == 8 and len(set(declared)) == 8 and
         sum(p.startswith("src/") for p in declared) == 5 and
         sum(p.startswith("tests/") for p in declared) == 3 and
         all((root / p).is_file() for p in declared),
         "Phase01: 5 production and 3 test projects must exist in solution")

    core = core_override if core_override is not None else (
        root / ".github/workflows/windows-ci.yml").read_text(encoding="utf-8")
    release = release_override if release_override is not None else (
        root / ".github/workflows/windows-release.yml").read_text(encoding="utf-8")
    for label, workflow in [("Core",core), ("Release",release)]:
        need("pull_request:" in workflow and "branches: ['main']" in workflow,
             f"Phase01: {label} must run on both PR and main")
        need("dotnet test tests/AlTamam.IntegrationTests/" in workflow and
             "dotnet test tests/AlTamam.Tests/" in workflow,
             f"Phase01/02: {label} must run real integration and main suites")
        need("test-phase00-01-02-baseline.py" in workflow,
             f"Phase00/01/02: {label} missing cross-phase CI guard")
        need("if ($LASTEXITCODE -ne 0)" in workflow,
             f"Phase01: {label} lacks explicit subprocess failure check")
    need(re.search(r"(?m)^  core-tests:\s*\n\s+needs: manifest-contract",core) is not None,
         "Phase01: Core contract guard must precede Core tests")
    need(re.search(r"(?m)^  build-test:\s*\n(?:.*\n){0,3}\s+needs: preflight",release) is not None,
         "Phase01: Release preflight must precede full tests")
    need(re.search(r"(?m)^  package:\s*\n(?:.*\n){0,3}\s+needs: build-test",release) is not None,
         "Phase01: Release package must depend on successful full tests")
    need(re.search(r"(?m)^  handover-evidence:\s*\n(?:.*\n){0,6}\s+needs: package",release) is not None,
         "Phase01: independent four-package SHA256 audit must depend on packages")
    need("windows-latest" in core and "windows-latest" in release,
         "Phase01/02: actual Windows jobs are mandatory")

    db = db_override if db_override is not None else (
        root / "src/AlTamam.Infrastructure/Persistence/AlTamamDbContext.cs"
    ).read_text(encoding="utf-8")
    runner = runner_override if runner_override is not None else (
        root / "src/AlTamam.Infrastructure/Persistence/Migrations/SchemaMigrationRunner.cs"
    ).read_text(encoding="utf-8")
    versions = [int(v) for v in re.findall(
        r'new\((\d+),\s*"V\d{3}_',db)]
    need(versions == list(range(1,13)),
         "Phase02: V001-V012 canonical migration versions must be contiguous")
    migration_paths = sorted((root / "src/AlTamam.Infrastructure/Persistence/Migrations").glob("V[0-9][0-9][0-9]_*.sql"))
    need(len(migration_paths) == 12,
         "Phase02: exactly twelve unchanged shipped SQL resource versions are expected")
    need('new SchemaMigrationRunner(CanonicalMigrations).VerifyRecordedHashes(connection)' in db,
         "Phase02: current-schema reopening must verify every pinned migration")
    need('if (recorded is null) continue' not in runner and
         'Migration pin missing or mismatched' in runner,
         "Phase02: no missing/NULL migration SHA256 pin may be skipped")
    need('PRAGMA cipher_version;' in db and 'PRAGMA journal_mode=WAL;' in db,
         "Phase02: original SQLCipher and WAL contract missing")
    need('SQLitePCLRaw.bundle_e_sqlcipher' in (
        root / "src/AlTamam.Infrastructure/AlTamam.Infrastructure.csproj"
    ).read_text(encoding="utf-8"),
         "Phase02: approved SQLCipher bundle changed")
    return errors


class Phase000102CurrentContractTests(unittest.TestCase):
    def test_actual_current_checkout(self):
        self.assertEqual([], contracts())

    def test_release_without_test_dependency_is_rejected(self):
        path=ROOT/".github/workflows/windows-release.yml"
        release=path.read_text(encoding="utf-8")
        self.assertIn("needs: build-test",release)
        failures=contracts(release_override=release.replace(
            "needs: build-test","needs: preflight"))
        self.assertTrue(any("package must depend" in x for x in failures))

    def test_a_missing_migration_pin_guard_is_rejected(self):
        path=ROOT/"src/AlTamam.Infrastructure/Persistence/Migrations/SchemaMigrationRunner.cs"
        runner=path.read_text(encoding="utf-8").replace(
            "Migration pin missing or mismatched", "pin audit disabled")
        failures=contracts(runner_override=runner)
        self.assertTrue(any("missing/NULL" in x for x in failures))

    def test_source_master_archive_gap_is_not_falsely_approved(self):
        p=ROOT/"docs/implementation/AlTamam_QwenCoder_Master_Implementation_Prompt_EN.txt"
        ledger=(ROOT/"docs/implementation/PHASE00_01_02_CURRENT_RECONCILIATION_2026-11-02.md"
               ).read_text(encoding="utf-8")
        if not p.is_file():
            self.assertIn("SOURCE GAP — NOT CLOSED",ledger)
        self.assertIn("NO-GO",ledger)


if __name__ == "__main__":
    unittest.main(verbosity=2)
