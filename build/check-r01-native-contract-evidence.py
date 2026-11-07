#!/usr/bin/env python3
"""Require executed native lifecycle tests and every committed wire snapshot.

This gate covers this named 89-action slice, NOT all 104 manifest actions.
It consumes the real full-suite TRX; a metadata inventory cannot satisfy it.
"""
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SNAPSHOTS = ROOT / "tests/AlTamam.Tests/Bridge/Snapshots/R01NativeConfigurationResponseShapes.json"
PREFIX = "AlTamam.Tests.Bridge.R01Native"
CASES = {
    "ExcelLifecycleTests.Monthly_RealAllSitesAndFilteredWorkbooks_ExactlyMatchEveryPagedNativeCell",
    "ExcelLifecycleTests.PersonReports_RealSingleGroupFilteredAndPagedExcel_PreserveHistoricalIdentityAndTotals",
    "ExcelLifecycleTests.Import_RealWorkbookAnalyzePreviewCommitOutcomeAndIssues_PersistExactlyOneAcceptedPerson",
    "ConfigurationLifecycleTests.PersonnelArchive_RealPreviewCommit_HidesRosterWithoutErasingIdentity",
    "ConfigurationLifecycleTests.PersonnelIdentity_RealPreviewEdit_PreservesCanonicalNumberAndHistoricalIdentity",
    "ConfigurationLifecycleTests.SitesAndStatuses_CreateRenameRetireAndDeactivate_PreserveHistoryAndReceipts",
    "ConfigurationLifecycleTests.CustomColumns_RealCreateEditDeactivate_PreserveStableIdAndStoredValues",
    "ConfigurationLifecycleTests.IndependentSettings_RealWritesAndReads_KeepSeparateSchedulesAndReceipts",
    "ConfigurationLifecycleTests.UserAccounts_RealLifecycle_RetainsPersonLinkAndOneAuditPerMutation",
    "ConfigurationLifecycleTests.AuditPresentation_RealEditHideAndHistory_PreserveCanonicalEvidence",
    "OperationalLifecycleTests.Attendance_DraftPreviewCommitRevise_RealReadModelsAndHistoricalAliasesAgree",
    "OperationalLifecycleTests.Transfers_RealInternalAndExternalCommits_PreserveDatedMembershipAndArchives",
    "OperationalLifecycleTests.Separation_RealServerDelayCommit_RetainsReasonAndHistoryWithoutInventedAttendance",
}
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def validate(trx, snapshots):
    canonical = {row["action"] for row in json.loads(
        (ROOT / "src/AlTamam.WebUI/action-manifest.json").read_text(encoding="utf-8"))["actions"]}
    if not isinstance(snapshots, dict) or not snapshots or set(snapshots) - canonical:
        raise ValueError("Snapshots must name canonical manifest actions")
    if any(not isinstance(values, list) or not values or
           any(not isinstance(value, str) or not value for value in values)
           for values in snapshots.values()):
        raise ValueError("Every action requires nonempty response-shape snapshots")
    results = ET.parse(trx).findall(".//t:UnitTestResult", NS)
    native = [row for row in results if row.get("testName", "").startswith(PREFIX)]
    matched = {row.get("testName")[len(PREFIX):]: row for row in native}
    if len(native) != len(matched):
        raise ValueError("Duplicate native test case evidence")
    missing = CASES - matched.keys()
    if missing:
        raise ValueError("Native test cases not executed: " + ", ".join(sorted(missing)))
    observed = set()
    for name in sorted(CASES):
        result = matched[name]
        if result.get("outcome") != "Passed":
            raise ValueError("Native test did not pass: " + name)
        stdout = result.find("t:Output/t:StdOut", NS)
        marker = "R01_NATIVE_SHAPE "
        if stdout is None or not stdout.text or marker not in stdout.text:
            raise ValueError("Native response output missing: " + name)
        for line in stdout.text.splitlines():
            if marker not in line:
                continue
            action, separator, shape = line.split(marker, 1)[1].strip().partition(" ")
            if not separator or action not in snapshots or shape not in snapshots[action]:
                raise ValueError("Native response snapshot mismatch: " + action)
            observed.add(action)
    if set(snapshots) != observed:
        raise ValueError("Native actions not observed: " + ", ".join(sorted(set(snapshots) - observed)))
    return observed


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: check-r01-native-contract-evidence.py <actual-suite.trx>")
    try:
        found = validate(Path(sys.argv[1]), json.loads(SNAPSHOTS.read_text(encoding="utf-8")))
    except (OSError, ValueError, ET.ParseError) as error:
        raise SystemExit("R01 NATIVE CONTRACT EVIDENCE FAIL: " + str(error)) from error
    print(f"R01 NATIVE CONTRACT SLICE PASS: {len(CASES)} real native cases / {len(found)} actions; not full R01 closure")
