#!/usr/bin/env python3
"""Live, non-destructive Phase 00/01/02 guard for owner-approved repository invariants.

Historical dated inventory documents remain snapshots. This guard checks shipped
source, both actual Windows workflows and the existing SQLCipher schema/provider.
It does NOT infer application UAT, full route functionality, restore GO or signoff.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EXPECTED_PROJECTS = {
    "src/AlTamam.Domain/AlTamam.Domain.csproj",
    "src/AlTamam.Contracts/AlTamam.Contracts.csproj",
    "src/AlTamam.Application/AlTamam.Application.csproj",
    "src/AlTamam.Infrastructure/AlTamam.Infrastructure.csproj",
    "src/AlTamam.DesktopHost/AlTamam.DesktopHost.csproj",
    "tests/AlTamam.Tests/AlTamam.Tests.csproj",
    "tests/AlTamam.UnitTests/AlTamam.UnitTests.csproj",
    "tests/AlTamam.IntegrationTests/AlTamam.IntegrationTests.csproj",
}


def collect(root: Path) -> dict:
    def read(path: str) -> str:
        return (root / path).read_text(encoding="utf-8-sig")

    routes = json.loads(read("src/AlTamam.WebUI/screen-manifest.json"))
    actions = json.loads(read("src/AlTamam.WebUI/action-manifest.json"))
    db = read("src/AlTamam.Infrastructure/Persistence/AlTamamDbContext.cs")
    packages = read("Directory.Packages.props")
    bridge = read("src/AlTamam.Application/Bridge/BridgeHandlerCatalog.cs")
    core = read(".github/workflows/windows-ci.yml")
    release = read(".github/workflows/windows-release.yml")
    v012 = read("src/AlTamam.Infrastructure/Persistence/Migrations/V012_AutoAttendanceOutcomeCounts.sql")
    v013 = read("src/AlTamam.Infrastructure/Persistence/Migrations/V013_MovementAttendanceImmutableKeys.sql")
    v014 = read("src/AlTamam.Infrastructure/Persistence/Migrations/V014_AttendanceStatusPeriods.sql")
    slnx = read("AlTamam.slnx")
    schema_match = re.search(r"CurrentSchemaVersion\s*=\s*(\d+)", db)
    provider_match = re.search(
        r'<PackageVersion\s+Include="SQLitePCLRaw\.bundle_e_sqlcipher"\s+Version="([^"]+)"',
        packages)
    migrations = [
        int(v) for v in re.findall(r'new\((\d+),\s*"V\d{3}_', db)
    ]
    migration_dir = root / "src/AlTamam.Infrastructure/Persistence/Migrations"
    embedded = [
        int(path.name[1:4]) for path in migration_dir.glob("V[0-9][0-9][0-9]_*.sql")
    ]
    workflows = {p.name for p in (root / ".github/workflows").glob("*.yml")}
    phase2_test = "tests/AlTamam.IntegrationTests/AlTamam.IntegrationTests.csproj"
    projects = set(re.findall(r'<Project\s+Path="([^"]+)"', slnx))
    return {
        "screen_count": len(routes["screens"]),
        "declared_screen_count": routes["screenCount"],
        "action_count": len(actions["actions"]),
        "unique_actions": len({x["action"] for x in actions["actions"]}),
        "registered_migrations": migrations,
        "embedded_migrations": embedded,
        "schema_version": int(schema_match.group(1)) if schema_match else None,
        "sqlcipher_version": provider_match.group(1) if provider_match else None,
        "two_workflows": workflows == {"windows-ci.yml", "windows-release.yml"},
        "core_tests": all(x in core for x in (
            "needs: manifest-contract", "dotnet build AlTamam.slnx",
            "dotnet test tests/AlTamam.Tests/AlTamam.Tests.csproj")),
        "release_test_gate": all(x in release for x in (
            "needs: preflight", "needs: build-test",
            "Full deterministic test suite", "Phase 15 independent four-package customer SHA256 audit")),
        "four_package_matrix": all(x in release for x in (
            "runtime: win-x86", "runtime: win-x64",
            "Build Portable ZIP", "compile installer")),
        "phase02_integration_on_both": phase2_test in core and phase2_test in release,
        "current_schema_pin_verified": bool(re.search(
            r"if\s*\(hasVersionTable\)\s*new\s+SchemaMigrationRunner\(CanonicalMigrations\)\.VerifyRecordedHashes\(connection\)", db)),
        "destructive_restore_wired": bool(
            re.search(r'"RestoreDatabaseAtomically"\s*=>', bridge)),
        "offline_contract": (
            actions.get("offlineModeEnforced") is True and
            actions.get("allowExternalNetwork") is False and
            actions.get("virtualHostOrigin") == "https://manger.adwas.example" and
            "connect-src 'none'" in actions.get("contentSecurityPolicy", "")),
        "projects": sorted(projects),
        "all_projects_exist": all((root / p).is_file() for p in projects),
        "cipher_pragma": "PRAGMA cipher_version;" in db and "PRAGMA journal_mode=WAL" in db,
        "v013_storage_constraints": all(k in v013 for k in (
            "TR_PersonnelMovements_AllowedType_Insert",
            "TR_AttendanceRecords_ImmutablePersonDate_Update",
            "UX_PersonAssignments_OneOpenPerPerson")),
        "v014_dated_attendance": all(k in v014 for k in (
            "CREATE TABLE AttendanceStatusPeriods",
            "CHECK(StatusId IN ('STS-LEAVE','STS-PERMISSION'))",
            "TR_AttendanceStatusPeriods_NoOverlap_Insert",
            "TR_AttendanceStatusPeriods_NoOverlap_Update",
            "ALTER TABLE AttendanceDrafts ADD COLUMN PeriodFromLocalDate",
            "ALTER TABLE AttendanceDrafts ADD COLUMN PeriodToLocalDate")),
        "legacy_nullable_v012": all(
            (key + " INTEGER NULL CHECK (" + key + " >= 0)") in v012
            for key in ("DefaultedCount", "SkippedManualCount", "FailedCount")),
    }


def verify(f: dict) -> list[str]:
    errors = []
    if f["screen_count"] != 69 or f["declared_screen_count"] != 69:
        errors.append("screen inventory: expected 69 canonical routes")
    if f["action_count"] != 103 or f["unique_actions"] != 103:
        errors.append("action inventory: expected 103 unique canonical actions")
    if f["registered_migrations"] != list(range(1, 15)):
        errors.append("migration registration: expected contiguous V001–V014")
    if sorted(f["embedded_migrations"]) != list(range(1, 15)):
        errors.append("migration resources: every V001–V014 SQL file must ship")
    if f["schema_version"] != 14:
        errors.append("migration current schema version must match V014")
    if f["sqlcipher_version"] != "2.1.10":
        errors.append("SQLCipher provider changed without approved migration")
    if f["destructive_restore_wired"]:
        errors.append("destructive restore is owner-gated and must remain disabled")
    if not f["two_workflows"]:
        errors.append("Phase 01 requires exactly two parallel Windows workflows")
    if not f["core_tests"]:
        errors.append("Phase 01 Core solution build and real tests gate missing")
    if not f["release_test_gate"]:
        errors.append("Phase 01 Release must test before packaging + independent SHA256")
    if not f["four_package_matrix"]:
        errors.append("Phase 01 requires x86/x64 Portable and Installer outputs")
    if not f["phase02_integration_on_both"]:
        errors.append("Phase 02 independent SQLCipher integration suite absent from Core or Release")
    if not f["offline_contract"]:
        errors.append("Offline WebView2 manifest/CSP/origin contract drift")
    if set(f["projects"]) != EXPECTED_PROJECTS or not f["all_projects_exist"]:
        errors.append("Phase 00 solution project inventory drift")
    if not f["cipher_pragma"]:
        errors.append("Phase 02 SQLCipher/WAL startup integrity contract drift")
    if not f["current_schema_pin_verified"]:
        errors.append("Phase 02 already-current SQLCipher databases must verify historical migration pins on startup")
    if not f["v013_storage_constraints"]:
        errors.append("Section 09 movement/attendance/assignment storage constraints missing")
    if not f["v014_dated_attendance"]:
        errors.append("V014 leave/permission effective-date and draft storage constraints missing")
    if not f["legacy_nullable_v012"]:
        errors.append("Phase 02 V012 legacy unknown-count preservation changed")
    return errors


def main() -> int:
    try:
        facts = collect(ROOT)
        errors = verify(facts)
    except (OSError, ValueError, KeyError, TypeError) as exc:
        print("PHASE 00–02 GUARD FAIL: cannot inspect repository:", exc)
        return 1
    if errors:
        print("PHASE 00–02 GUARD FAIL:")
        for error in errors:
            print(" -", error)
        return 1
    print("PHASE 00–02 GUARD PASS: 69 routes, 103 actions, 8 projects, 2 gated Windows workflows, V001–V014, SQLCipher pinned, restore disabled.")
    print("This is a source/CI invariant check, NOT clean-machine or destructive restore acceptance.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
