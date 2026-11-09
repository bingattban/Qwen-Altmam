#!/usr/bin/env python3
"""R18: negative-path validation of an immutable, audited CI candidate ledger.
This is inventory schema testing, NOT independent rehash of customer binaries
and never a physical customer acceptance result.
"""
import copy
import importlib.util
import json
import unittest
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
LEDGER=ROOT/"artifacts/R17_MAIN_AUDITED_CANDIDATE_2026-11-09.json"
VERIFIER=ROOT/"build/validate-frozen-candidate.py"

spec=importlib.util.spec_from_file_location("frozen_candidate",VERIFIER)
assert spec is not None and spec.loader is not None
auditor=importlib.util.module_from_spec(spec)
spec.loader.exec_module(auditor)

class FrozenCandidateContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.original=json.loads(LEDGER.read_text(encoding="utf-8"))

    def test_real_audited_main_snapshot_has_frozen_source_and_four_real_file_hashes(self):
        auditor.validate(self.original)
        self.assertEqual(self.original["testedCheckoutSha"],
            "859cc9c7e889d73f0f23debd0235c3c912f397c8")
        self.assertEqual(self.original["sourceHeadSha"],self.original["testedCheckoutSha"])
        self.assertEqual(self.original["githubRunId"],"42698618694")
        self.assertEqual(self.original["coreRunId"],"42698618350")
        self.assertEqual(self.original["version"],"0.0.1135-ci")
        self.assertEqual(self.original["releasePackageCount"],4)
        self.assertEqual(len(self.original["releaseFiles"]),4)
        self.assertEqual(self.original["independentCleanOfflineWindowsAcceptance"],"NOT_EXECUTED")
        self.assertEqual(self.original["ownerSignedFinalRelease"],False)

    def test_tampered_or_duplicated_inventory_is_rejected(self):
        variations={
          "checkout-mismatch":lambda d:d.update(sourceHeadSha="f"*40),
          "missing-package":lambda d:d["releaseFiles"].pop(),
          "duplicate-package":lambda d:d["releaseFiles"].__setitem__(1,
              copy.deepcopy(d["releaseFiles"][0])),
          "wrong-arch":lambda d:d["releaseFiles"][0].update(runtimeIdentifier="win-x64"),
          "wrong-name":lambda d:d["releaseFiles"][0].update(file="unchecked.zip"),
          "bad-digest":lambda d:d["releaseFiles"][0].update(sha256="0"*63+"!"),
          "bad-size":lambda d:d["releaseFiles"][0].update(bytes=0),
          "misleading-status":lambda d:d.update(status="PRODUCTION_ACCEPTED"),
          "fake-signed-release":lambda d:d.update(ownerSignedFinalRelease=True),
          "fake-physical-UAT":lambda d:d.update(independentCleanOfflineWindowsAcceptance="PASS"),
          "fake-cross-PC":lambda d:d.update(differentMachineEncryptedRecovery="PASS"),
          "fake-power-cut":lambda d:d.update(realPowerFailureRecovery="PASS"),
        }
        for description,mutate in variations.items():
            with self.subTest(description=description):
                data=copy.deepcopy(self.original)
                mutate(data)
                with self.assertRaises(ValueError):
                    auditor.validate(data)

    def test_current_no_go_ledger_points_to_exact_latest_verified_main_and_no_false_uat(self):
        doc=(ROOT/"docs/release/GO_NO_GO.md").read_text(encoding="utf-8")
        for evidence in ("R17_MAIN_AUDITED_CANDIDATE_2026-11-09.json",
                         "859cc9c7e889d73f0f23debd0235c3c912f397c8",
                         "42698618350","42698618694","NO-GO",
                         "NOT EXECUTED","2026-11-10"):
            with self.subTest(evidence=evidence):
                self.assertIn(evidence,doc)
        self.assertIn("Independent four-file SHA-256",doc)

if __name__=="__main__":
    unittest.main(verbosity=2)
