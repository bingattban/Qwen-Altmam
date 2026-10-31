#!/usr/bin/env python3
"""Section 05 / Phase 01: fail closed if CI speedups silently weaken test/release gates.

This runs in BOTH existing Windows preflight jobs. It has no third pipeline.
"""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CORE = ROOT / ".github/workflows/windows-ci.yml"
RELEASE = ROOT / ".github/workflows/windows-release.yml"


def validate(core: str, release: str) -> list[str]:
    issues = []
    if "  compile-core:\n" in core:
        issues.append("Core still starts a second Windows runner for duplicate solution compilation")
    if "  core-tests:\n    needs: manifest-contract\n" not in core:
        issues.append("Core full test job must depend on the manifest/security gate")
    section = core.split("  core-tests:\n", 1)[-1] if "  core-tests:\n" in core else ""
    if section.count("dotnet restore AlTamam.slnx") != 1:
        issues.append("Core must restore the full solution exactly once per runner")
    if section.count("dotnet build AlTamam.slnx") != 1 or "dotnet build AlTamam.slnx --configuration Release --no-restore" not in section:
        issues.append("Core must explicitly compile the full solution after restore")
    if section.count("dotnet test tests/AlTamam.Tests/AlTamam.Tests.csproj") != 2:
        issues.append("Core must run its full suite and the 10k diagnostic benchmark")
    if "--configuration Release --no-build --no-restore" not in section:
        issues.append("Core full tests must reuse the validated build outputs")
    if "-c Release --no-build --no-restore" not in section:
        issues.append("Core benchmark must reuse the same build outputs")
    if "TestResults/core-tests.trx" not in section or "ResultSummary.Counters.total" not in section:
        issues.append("Core must reject zero-executed full test suites")
    if "if ($count -lt 1)" not in section:
        issues.append("Core full test count guard must fail closed")
    if "dotnet test tests/AlTamam.Tests/AlTamam.Tests.csproj -c $env:CONFIGURATION --no-build --no-restore" not in release:
        issues.append("Release must execute the established full xUnit suite without building or restoring")
    if "dotnet test tests/AlTamam.IntegrationTests/AlTamam.IntegrationTests.csproj -c $env:CONFIGURATION --no-build --no-restore" not in release:
        issues.append("Release must execute the independent Phase 02 SQLCipher suite before packaging")
    if "dotnet test tests/AlTamam.IntegrationTests/AlTamam.IntegrationTests.csproj" not in core:
        issues.append("Core must execute the independent Phase 02 integration project")
    if "phase02-integration.trx" not in core or "phase02-integration.trx" not in release:
        issues.append("Both workflows must retain Phase 02 real TRX evidence")
    if "  preflight:\n" not in release or "  build-test:\n    name: Build + full test suite on Windows x64 (15.01)\n    needs: preflight" not in release:
        issues.append("Release full test stage must follow preflight")
    if "  package:\n" not in release:
        issues.append("Release x86/x64 packaging matrix is missing")
    else:
        package = release.split("  package:\n", 1)[1].split("  clean-windows-acceptance:\n", 1)[0]
        if "    needs: build-test" not in package:
            issues.append("Release packages MUST depend on full green Release tests")
        for required in ("runtime: win-x64", "runtime: win-x86",
                         "Build Portable ZIP", "compile installer", "SHA-256 checksums",
                         "Upload standalone Portable", "Upload standalone Installer",
                         "Upload checksums and build metadata"):
            if required not in package:
                issues.append("Release package output/gate missing: " + required)
        for label in ("Upload standalone Portable", "Upload standalone Installer"):
            part = package.split("- name: " + label, 1)
            if len(part) != 2 or "compression-level: 0" not in part[1].split("- name:", 1)[0]:
                issues.append("Already-compressed ZIP/EXE upload must avoid redundant compression: " + label)
        raw = package.split("- name: Retain raw publish dir for acceptance testing", 1)
        if len(raw) != 2 or "compression-level: 1" not in raw[1].split("- name:", 1)[0]:
            issues.append("Raw publish artifact should use fast compression, retaining acceptance evidence")
    for label, workflow, jobs in (
        ("Core", core, ("  core-tests:",)),
        ("Release", release, ("  preflight:", "  build-test:", "  package:")),
    ):
        for job in jobs:
            segment = workflow.split(job, 1)
            if len(segment) != 2:
                issues.append(label + " missing job " + job.strip())
                continue
            body = re.split(r"(?m)^  [a-z][a-z0-9-]*: *$", segment[1], maxsplit=1)[0]
            if "actions/setup-dotnet@v4" not in body:
                issues.append(label + " missing .NET bootstrap in " + job.strip())
            if "cache: true" not in body or "cache-dependency-path: Directory.Packages.props" not in body:
                issues.append(label + " must cache immutable NuGet package downloads in " + job.strip())
    for workflow in (core, release):
        if "pwsh -NoProfile -File ./build/test-webview-ui-e2e.ps1" not in workflow:
            issues.append("A real WPF/WebView2 UI E2E must run in both Windows test phases")
        if "python build/test-ci-exit-guards.py\n          if ($LASTEXITCODE -ne 0)" not in workflow:
            issues.append("Existing immediate Node/Python failure guard must be retained")
        if "python build/test-ci-performance-contract.py\n          if ($LASTEXITCODE -ne 0)" not in workflow:
            issues.append("New performance safety guard must run in BOTH preflight gates")
        if "cancel-in-progress: true" not in workflow:
            issues.append("Obsolete PR head runs must be cancelled to avoid overload")
    return issues


def self_test(core: str, release: str) -> None:
    broken = core.replace("--configuration Release --no-build --no-restore",
                          "--configuration Release --no-restore", 1)
    assert any("reuse the validated build outputs" in x for x in validate(broken, release))
    broken_release = release.replace("    needs: build-test", "    needs: preflight", 1)
    assert any("MUST depend on full green" in x for x in validate(core, broken_release))
    unsafe = release.replace("compression-level: 0", "compression-level: 6", 1)
    assert any("redundant compression" in x for x in validate(core, unsafe))


if __name__ == "__main__":
    core, release = CORE.read_text(encoding="utf-8"), RELEASE.read_text(encoding="utf-8")
    self_test(core, release)
    issues = validate(core, release)
    if issues:
        raise SystemExit("SECTION05 CI OPTIMIZATION GUARD FAIL:\n" + "\n".join(issues))
    print("SECTION05 CI OPTIMIZATION GUARD PASS: Core build reuse/cache, both full test gates, x86/x64 packages")
