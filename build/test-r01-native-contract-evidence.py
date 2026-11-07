#!/usr/bin/env python3
"""Negative-path tests for evidence validation, not SQLCipher acceptance substitutes."""
import copy
import importlib.util
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

SPEC = importlib.util.spec_from_file_location("r01_evidence", Path(__file__).with_name("check-r01-native-contract-evidence.py"))
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)


class NativeEvidenceGuard(unittest.TestCase):
    def fixture(self, missing_case=False, failed=False, missing_action=False, wrong_shape=False, duplicate=False, configuration_only=False):
        namespace = gate.NS["t"]
        root = ET.Element("{" + namespace + "}TestRun")
        results = ET.SubElement(root, "{" + namespace + "}Results")
        for index, case in enumerate(sorted(gate.CASES)):
            if configuration_only and case.startswith("OperationalLifecycleTests."):
                continue
            if index == 0 and missing_case:
                continue
            row = ET.SubElement(results, "{" + namespace + "}UnitTestResult",
                testName=gate.PREFIX + case, outcome="Failed" if failed and index == 0 else "Passed")
            output = ET.SubElement(row, "{" + namespace + "}Output")
            stdout = ET.SubElement(output, "{" + namespace + "}StdOut")
            stdout.text = "R01_NATIVE_SHAPE QuerySites array<object>"
            if not missing_action:
                stdout.text += "\nR01_NATIVE_SHAPE CreateSite " + ("object<wrong>" if wrong_shape else "object<siteId>")
        if duplicate:
            results.append(copy.deepcopy(list(results)[0]))
        return ET.ElementTree(root)

    def check(self, **kwargs):
        with tempfile.TemporaryDirectory() as folder:
            trx = Path(folder) / "native.trx"
            self.fixture(**kwargs).write(trx, encoding="utf-8")
            return gate.validate(trx, {"QuerySites": ["array<object>"], "CreateSite": ["object<siteId>"]})

    def test_complete_passed_execution_is_accepted(self):
        self.assertEqual(self.check(), {"QuerySites", "CreateSite"})

    def test_missing_test_cannot_be_replaced_by_other_output(self):
        with self.assertRaisesRegex(ValueError, "not executed"):
            self.check(missing_case=True)

    def test_configuration_output_cannot_replace_operational_execution(self):
        with self.assertRaisesRegex(ValueError, "not executed"):
            self.check(configuration_only=True)

    def test_failed_case_cannot_be_masked_by_complete_action_lines(self):
        with self.assertRaisesRegex(ValueError, "did not pass"):
            self.check(failed=True)

    def test_missing_action_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "not observed"):
            self.check(missing_action=True)

    def test_duplicate_test_evidence_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            self.check(duplicate=True)

    def test_changed_response_shape_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "snapshot mismatch"):
            self.check(wrong_shape=True)


if __name__ == "__main__":
    unittest.main()
