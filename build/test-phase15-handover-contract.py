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

    def test_audited_main_candidate_evidence_is_not_production_approval(self):
        """Validate retained SHA evidence and explicitly independent acceptance."""
        import re
        proof = json.loads(read("artifacts/PHASE15_AUDITED_CANDIDATE_2026-11-03.json"))
        sha = proof["sourceHeadSha"]
        self.assertRegex(sha, r"^[0-9a-f]{40}$")
        self.assertEqual(proof["testedCheckoutSha"], sha)
        self.assertEqual(proof["status"], "CI_CANDIDATE_ONLY_NOT_PRODUCTION_ACCEPTANCE")
        self.assertEqual(proof["version"], "0.0.909-ci")
        self.assertEqual(proof["core"]["conclusion"], "success")
        self.assertEqual(proof["release"]["conclusion"], "success")
        self.assertEqual(proof["release"]["fourPackageAudit"], "success")
        self.assertEqual(proof["release"]["cleanMachineGate"], "skipped")
        packages = proof["releaseFiles"]
        self.assertEqual(len(packages), 4)
        self.assertEqual({(p["runtimeIdentifier"], p["class"]) for p in packages},
                         {("win-x86", "Portable"), ("win-x86", "Installer"),
                          ("win-x64", "Portable"), ("win-x64", "Installer")})
        for package in packages:
            self.assertRegex(package["sha256"], r"^[0-9a-f]{64}$")
            self.assertGreater(package["bytes"], 1000000)
        self.assertEqual(proof["benchmark"]["attendanceRecords"], 550000)
        self.assertEqual(proof["benchmark"]["fullGui10000People31DayEndToEnd"], "NOT_EXECUTED")
        self.assertEqual(proof["benchmark"]["excelExportThroughput"], "NOT_EXECUTED")
        self.assertFalse(proof["ownerProductionApproval"])
        for name in ("docs/release/GO_NO_GO.md",
                     "docs/release/PHASE15_ACCEPTANCE_MATRIX.md"):
            body = read(name)
            self.assertIn(sha, body)
            self.assertIn("NO-GO", body)
            self.assertIn("NOT EXECUTED", body)

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


    def test_section07_runtime_route_semantics(self):
        """Prevent retired route labels, wrong default hour and inline status edits."""
        data = json.loads((WEB / "screen-manifest.json").read_text(encoding="utf-8"))
        routes = {row["screenId"]: row for row in data["screens"]}
        self.assertEqual(len(routes), 69)
        self.assertNotIn("screen_020", routes)
        self.assertEqual(routes["screen_025"]["routeKey"], "PersonnelReport")
        self.assertEqual(routes["screen_026"]["titleAr"], "تصدير تقرير الأفراد")
        self.assertIn("تصدير تقرير الأفراد", read("src/AlTamam.WebUI/screens/Administration/PersonnelManagement/PersonnelReport/ExportPersonReport/index.html"))
        self.assertEqual(routes["screen_070"]["routeKey"], "AddPerson")
        self.assertIn("08:00", routes["screen_069"]["breadcrumbAr"])
        self.assertNotIn("غير المحضرين", routes["screen_003"]["breadcrumbAr"])
        scheduler = read("src/AlTamam.Application/Scheduling/AutoAttendanceService.cs")
        self.assertIn("DefaultDueHour = 8", scheduler)
        ui = read("src/AlTamam.WebUI/assets/js/functional-screens.js")
        self.assertNotIn("const batchStatus=", ui)
        self.assertNotIn("statusSelect.setAttribute", ui)
        self.assertIn("const picked=await choose([", ui)
        self.assertIn('person.committedAtUtc||"—"', ui)
        backend = read("src/AlTamam.Application/Bridge/DailyMonthlyReadService.cs")
        self.assertIn("ar.CommittedAtUtc", backend)
        self.assertIn('["committedAtUtc"] = reader.IsDBNull(12)', backend)

    def test_current_main_release_evidence_distinguishes_ci_from_field_uat(self):
        """The immutable November main candidate is not an independent field GO."""
        main_sha = "ed255a1420c047e8eb7b929bd3bdc509eeffa263"
        for name in ("docs/release/GO_NO_GO.md",
                     "docs/release/FINAL_ACCEPTANCE_REPORT.md"):
            with self.subTest(path=name):
                body = read(name)
                for required in (main_sha, "43062807272", "43062807273",
                                 "0.0.1172-ci", "NO-GO", "NOT EXECUTED",
                                 "manual", "WIN-01", "WIN-10"):
                    self.assertIn(required, body,
                        "Current real CI must not be substituted for independent acceptance")
        self.assertIn("SKIPPED", read("docs/release/GO_NO_GO.md"))

if __name__ == "__main__":
    unittest.main(verbosity=2)
