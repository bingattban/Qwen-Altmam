#!/usr/bin/env python3
"""Fail closed if the real .NET/SQLCipher 10k × multi-year diagnostic did not
execute and write an exact-SHA measured JSON file on Windows. This does NOT
constitute customer GUI acceptance, XLSX benchmark or final production GO."""
import json
import re
import sys
from pathlib import Path


def verify(path: Path, checkout: str, head: str) -> list[str]:
    issues = []
    if not re.fullmatch(r"[0-9a-f]{40}", checkout) or not re.fullmatch(r"[0-9a-f]{40}", head):
        return ["invalid Git SHA argument"]
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as exc:
        return ["measured diagnostic file absent or invalid: " + str(exc)]
    for key, wanted in (
        ("schema", "AlTamam.Phase15.ThreeYearSqlCipherDiagnostics.v1"),
        ("status", "MEASURED_SYNTHETIC_SQLCIPHER_NOT_REAL_10K_GUI_ACCEPTANCE"),
        ("personsActive", 10_000),
        ("historyMonthsPerPerson", 24),
        ("calendarYears", 3),
        ("fullMonthDaysPerPerson", 31),
        ("attendanceRecords", 550_000),
        ("measuredGroupedDailyRows", 31),
        ("productionMonthlyPageSize", 100),
        ("actualCheckoutSha", checkout),
        ("sourceHeadSha", head),
        ("fullGui10000People31DayEndToEnd", "NOT_EXECUTED"),
        ("excelExportThroughput", "MEASURED_SYNTHETIC_10000_PERSON_XLSX_NOT_FULL_GUI_ACCEPTANCE"),
        ("monthlyExcelExportPeople", 10_000),
        ("monthlyExcelXlsxWorksheetRows", 10_003),
        ("monthlyExcelNoPartialFiles", True),
        ("physicalPowerFailure", "NOT_EXECUTED"),
    ):
        if data.get(key) != wanted:
            issues.append(f"unexecuted, forged or mismatched Phase15 performance field: {key}")
    for key in ("seedElapsedMs", "seedRowsPerSecond", "grouped31DayElapsedMs",
                "productionMonthlyFirstPageElapsedMs", "workingSetBytes"):
        value = data.get(key)
        if not isinstance(value, (int, float)) or not (0 < value < 1e13):
            issues.append("absent or impossible measured value: " + key)
    for key in ("monthlyExcelExportElapsedMs", "monthlyExcelXlsxBytes"):
        value = data.get(key)
        if not isinstance(value, (int, float)) or not (0 < value < 1e13):
            issues.append("absent or impossible measured Excel value: " + key)
    if not re.fullmatch(r"[0-9a-f]{64}", str(data.get("monthlyExcelXlsxSha256", ""))):
        issues.append("missing full native XLSX SHA-256 from the measured export")
    if "Windows" not in str(data.get("os")):
        issues.append("benchmark did not run on Windows")
    if not str(data.get("githubRunId", "")).isdigit():
        issues.append("missing concrete GitHub run ID")
    return issues


if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit("usage: check-phase15-perf-evidence.py MEASURED_JSON CHECKOUT_SHA SOURCE_SHA")
    failures = verify(Path(sys.argv[1]), sys.argv[2], sys.argv[3])
    if failures:
        raise SystemExit("PHASE15 SQLCIPHER PERF EVIDENCE FAIL:\n" + "\n".join(failures))
    print("PHASE15 SQLCIPHER 10k × 3-year EVIDENCE PASS: same SHA and measured values; NOT GUI acceptance")
