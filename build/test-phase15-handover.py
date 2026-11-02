#!/usr/bin/env python3
"""Fail-closed synthetic fixtures for Phase 15 cross-arch candidate audit.
Fixture bytes are deliberately NOT a runnable Portable or Installer."""
import importlib.util
import hashlib
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("verify-phase15-handover.py")
SPEC = importlib.util.spec_from_file_location("phase15_handover", SCRIPT)
assert SPEC and SPEC.loader
MOD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MOD)
SHA = "a" * 40
SOURCE = "b" * 40
VERSION = "0.0.900-ci"


class CandidateAuditTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)
        for arch, (runtime, bitness) in MOD.ARCHITECTURES.items():
            checks = self.root / f"AlTamam-Checksums-{arch}-rc-{VERSION}"
            checks.mkdir()
            records, sums = [], []
            for kind, ext in (("Portable", ".zip"), ("Installer", ".exe")):
                folder = self.root / f"AlTamam-{kind}-{arch}-rc-{VERSION}"
                folder.mkdir()
                name = MOD.owner_filename(VERSION, arch, kind)
                data = f"SYNTHETIC ONLY {arch} {kind}".encode("ascii")
                (folder / name).write_bytes(data)
                sha = hashlib.sha256(data).hexdigest()
                records.append({"file": name, "sha256": sha, "bytes": len(data)})
                sums.append(f"{sha}  {name}")
            (checks / "SHA256SUMS.txt").write_text("\n".join(sums) + "\n")
            (checks / "BUILD_METADATA.json").write_text(json.dumps({
                "commit": SHA, "sourceHeadSha": SOURCE, "version": VERSION,
                "targetArchitecture": arch, "runtimeIdentifier": runtime,
                "architectureBitness": bitness, "files": records,
            }))

    def test_valid_exact_six_artifacts_produce_four_distinct_sha_values(self):
        out = MOD.audit(self.root, VERSION, SHA, SOURCE)
        self.assertEqual(out["releasePackageCount"], 4)
        self.assertEqual(len({f["sha256"] for f in out["releaseFiles"]}), 4)
        self.assertEqual(out["independentCleanOfflineWindowsAcceptance"], "NOT_EXECUTED")
        self.assertFalse(out["ownerSignedFinalRelease"])

    def test_wrong_source_and_version_are_rejected(self):
        for version, checkout, source in [
            (VERSION, SHA, "c" * 40),
            (VERSION, "x" * 40, SOURCE),
            ("0.0.901-ci", SHA, SOURCE),
        ]:
            with self.assertRaises(ValueError):
                MOD.audit(self.root, version, checkout, source)

    def test_tampered_portable_or_installer_fails(self):
        p = (self.root / f"AlTamam-Installer-Windows-32bit-rc-{VERSION}" /
             MOD.owner_filename(VERSION, "Windows-32bit", "Installer"))
        p.write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "SHA-256"):
            MOD.audit(self.root, VERSION, SHA, SOURCE)

    def test_missing_class_or_extra_untrusted_folder_rejected(self):
        p = (self.root / f"AlTamam-Portable-Windows-64bit-rc-{VERSION}" /
             MOD.owner_filename(VERSION, "Windows-64bit", "Portable"))
        p.unlink()
        with self.assertRaises(ValueError):
            MOD.audit(self.root, VERSION, SHA, SOURCE)
        p.write_bytes(b"SYNTHETIC ONLY Windows-64bit Portable")
        (self.root / "stray-reused-artifact").mkdir()
        with self.assertRaisesRegex(ValueError, "artifact count/name mismatch"):
            MOD.audit(self.root, VERSION, SHA, SOURCE)

    def test_stale_metadata_and_invalid_sum_are_rejected(self):
        p = self.root / f"AlTamam-Checksums-Windows-64bit-rc-{VERSION}" / "BUILD_METADATA.json"
        old = json.loads(p.read_text())
        old["commit"] = "f" * 40
        p.write_text(json.dumps(old))
        with self.assertRaisesRegex(ValueError, "provenance"):
            MOD.audit(self.root, VERSION, SHA, SOURCE)
        old["commit"] = SHA
        p.write_text(json.dumps(old))
        sums = p.with_name("SHA256SUMS.txt")
        sums.write_text("not a real sha\n")
        with self.assertRaises(ValueError):
            MOD.audit(self.root, VERSION, SHA, SOURCE)


if __name__ == "__main__":
    unittest.main()
