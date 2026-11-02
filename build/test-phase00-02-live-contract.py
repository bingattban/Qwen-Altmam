#!/usr/bin/env python3
"""Current-source guard for owner-approved Phases 00, 01 and 02.

Verifies actual tree/code/workflow contracts. Historical 2026-10-23
inventory documents are snapshots, never accepted as the live source.
This is STATIC evidence; Windows xUnit/SQLCipher and manual UAT remain separate.
"""
from pathlib import Path
import json
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


def check():
    screens = json.loads(read("src/AlTamam.WebUI/screen-manifest.json"))["screens"]
    actions = json.loads(read("src/AlTamam.WebUI/action-manifest.json"))["actions"]
    require(len(screens) == 69, "Phase 00: current screen inventory changed")
    require(len(actions) == 103, "Phase 00: current bridge action inventory changed")
    require(len({item["action"] for item in actions}) == 103,
            "Phase 00: duplicate canonical actions")
    require(len({item["owningService"] for item in actions}) == 11,
            "Phase 00: service inventory changed")

    sln = ET.fromstring(read("AlTamam.slnx"))
    paths = [node.attrib["Path"] for node in sln.iter("Project")]
    require(len(paths) == 8 and len(set(paths)) == 8,
            "Phase 01: expected exactly five production plus three test projects")
    require(sum(p.startswith("src/") for p in paths) == 5
            and sum(p.startswith("tests/") for p in paths) == 3,
            "Phase 01: approved solution/test topology changed")
    require("src/AlTamam.DesktopHost/AlTamam.DesktopHost.csproj" in paths
            and (ROOT / "src/AlTamam.WebUI").is_dir(),
            "Phase 01: approved DesktopHost/WebUI file ownership changed")

    migrations = sorted((ROOT / "src/AlTamam.Infrastructure/Persistence/Migrations")
                        .glob("V[0-9][0-9][0-9]_*.sql"))
    require(len(migrations) == 12, "Phase 02: migration count is not 12")
    versions = [int(p.name[1:4]) for p in migrations]
    require(versions == list(range(1, 13)),
            "Phase 02: schema migration sequence is not contiguous V001..V012")
    db = read("src/AlTamam.Infrastructure/Persistence/AlTamamDbContext.cs")
    match = re.search(r"CurrentSchemaVersion\s*=\s*(\d+)", db)
    require(match is not None and int(match.group(1)) == versions[-1],
            "Phase 02: current schema must match the final embedded resource")
    for migration in migrations:
        require(migration.name in db,
                "Phase 02: a migration is missing from the canonical registration: "
                + migration.name)
    require("runner.VerifyRecordedHashes(connection)" in db
            and "if (hasVersionTable)" in db,
            "Phase 02: current-schema startup must verify stored migration pins")
    packages = ET.fromstring(read("Directory.Packages.props"))
    cipher = [p.attrib.get("Version") for p in packages.iter("PackageVersion")
              if p.attrib.get("Include") == "SQLitePCLRaw.bundle_e_sqlcipher"]
    require(cipher == ["2.1.10"],
            "Phase 02: owner did not approve replacement of SQLCipher provider")

    catalog = read("src/AlTamam.Application/Bridge/BridgeHandlerCatalog.cs")
    require(not re.search(r'"RestoreDatabaseAtomically"\s*=>', catalog),
            "Phase 02: destructive native restore must remain intentionally disabled")

    core = read(".github/workflows/windows-ci.yml")
    release = read(".github/workflows/windows-release.yml")
    for name, content in (("Core", core), ("Release", release)):
        require("pull_request:" in content and "push:" in content,
                f"Phase 01: {name} must test PR head and new main")
        require("test-phase00-02-live-contract.py" in content,
                f"Phase 01: {name} missing current inventory preflight")
        require("phase02-integration.trx" in content,
                f"Phase 01: {name} missing executed Phase 02 integration evidence")
    require("needs: build-test" in release
            and "runtime: win-x86" in release
            and "runtime: win-x64" in release,
            "Phase 01: release must test before packaging x86 and x64")
    require((ROOT / "docs/implementation/PHASE00_01_02_LIVE_RECONCILIATION_2026-11-02.md").is_file(),
            "Phase 00: current repository reconciliation report is missing")
    print("PASS: Phase 00 69 routes/103 actions, Phase 01 5+3 projects/parallel Windows CI, "
          "Phase 02 V001..V012 SQLCipher + current-schema hash verification")


if __name__ == "__main__":
    check()
