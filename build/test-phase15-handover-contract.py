#!/usr/bin/env python3
"""Phase 15 non-destructive STATIC contract guard; NOT full Windows acceptance.

Never print a passing result here as evidence of native restore, live app actions,
clean-machine testing, real performance measurements or owner approval.
"""
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WEB = ROOT / "src" / "AlTamam.WebUI"
ADMINS = {"GeneralAdministration", "OrdinaryAdministration"}


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


class Phase15Contract(unittest.TestCase):
    def test_canonical_screen_inventory(self):
        manifest = json.loads((WEB / "screen-manifest.json").read_text(encoding="utf-8"))
        screens = manifest["screens"]
        ids = [x["screenId"] for x in screens]
        self.assertEqual(len(screens), manifest["screenCount"])
        self.assertEqual(len(ids), len(set(ids)))
        self.assertNotIn("screen_020", ids, "retired duplicate must stay removed")
        for screen_id, route in (("screen_070", "AddPerson"),
                                 ("screen_025", "PersonnelReport")):
            selected = [x for x in screens if x["screenId"] == screen_id]
            self.assertEqual(len(selected), 1)
            self.assertEqual(selected[0]["routeKey"], route)
            self.assertTrue((WEB / selected[0]["localAssetPath"]).is_file())
        self.assertEqual(next(x for x in screens if x["screenId"] == "screen_070")["kind"],
                         "embedded panel")

    def test_ui_wires_person_and_group_report(self):
        admin = read("src/AlTamam.WebUI/assets/js/admin-settings.js")
        report = read("src/AlTamam.WebUI/assets/js/personnel-report.js")
        self.assertIn('sourceRoute==="AddPerson"', admin)
        self.assertIn('invoke("CreatePerson"', admin)
        self.assertIn('invoke("BuildPersonLifetimeReport"', report)
        self.assertIn('ExportPersonLifetimeExcel', report)
        self.assertIn('person.personId', report)

    def test_admin_only_sensitive_operations(self):
        actions = json.loads((WEB / "action-manifest.json").read_text(encoding="utf-8"))["actions"]
        by_name = {x["action"]: x for x in actions}
        self.assertEqual(len(by_name), len(actions))
        for name in ("UpdateAuditPresentation", "DeleteAuditPresentation",
                     "PreviewClosedDayCorrection", "CommitClosedDayCorrection",
                     "PreviewAdministrativeAttendanceCatchUp",
                     "CommitAdministrativeAttendanceCatchUp"):
            with self.subTest(action=name):
                self.assertIn(name, by_name)
                self.assertEqual(set(by_name[name]["allowedRoles"]), ADMINS)
        self.assertIn("RestoreDatabaseAtomically", by_name)
        self.assertEqual(set(by_name["RestoreDatabaseAtomically"]["allowedRoles"]), ADMINS)
        # Manifest role permission does NOT enable native restore; see GO_NO_GO.

    def test_handover_files_present_with_no_false_release_claim(self):
        for name in ("CHANGELOG.md", "docs/release/GO_NO_GO.md",
                     "docs/release/PHASE15_ACCEPTANCE_MATRIX.md",
                     "docs/support/INSTALLATION_AND_FIRST_USE.md",
                     "docs/support/BACKUP_RECOVERY_MIGRATION_GUIDE.md",
                     "artifacts/README.md"):
            with self.subTest(path=name):
                self.assertTrue((ROOT / name).is_file())
        gate = read("docs/release/GO_NO_GO.md")
        self.assertIn("NO-GO", gate)
        self.assertIn("NOT SUBMITTED", gate)
        self.assertIn("PENDING", gate)
        self.assertIn("10,000", gate)
        self.assertIn("SKIPPED", gate)
        self.assertIn("RestoreDatabaseAtomically", gate)

    def test_both_windows_preflights_fail_closed(self):
        for workflow in (".github/workflows/windows-ci.yml",
                         ".github/workflows/windows-release.yml"):
            with self.subTest(workflow=workflow):
                content = read(workflow)
                self.assertEqual(content.count("python build/test-phase15-handover-contract.py"), 1)
                self.assertIn("Phase 15 static contract regression failed", content)
                self.assertIn("if ($LASTEXITCODE -ne 0)", content)


    def test_core_sqlcipher_evidence_check_has_source_sha_in_scope(self):
        """The test's step-local env must also be visible to the later verifier."""
        core = read(".github/workflows/windows-ci.yml")
        job_header = (
            "  core-tests:\n"
            "    needs: manifest-contract\n"
            "    runs-on: windows-latest\n"
            "    env:\n"
            "      ALTAMAM_SOURCE_HEAD_SHA: ${{ github.event.pull_request.head.sha || github.sha }}\n"
        )
        self.assertIn(job_header, core, "SHA must exist at job scope, not test step only")
        self.assertIn(
            "python build/check-phase15-perf-evidence.py "
            "PerformanceEvidence/phase15-10k-multi-year.json "
            "$env:GITHUB_SHA $env:ALTAMAM_SOURCE_HEAD_SHA",
            core,
        )


if __name__ == "__main__":
    unittest.main(verbosity=2)
