#!/usr/bin/env python3
"""Tests-first Phase 14 distribution copy, provenance and tamper negatives.
Synthetic bytes are ONLY fixtures; these never claim to be executable Windows
applications. Actual Windows package execution is enforced separately in CI.
"""
import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MODULE = ROOT / "build/stage-phase14-distributions.py"
spec = importlib.util.spec_from_file_location("phase14_stage", MODULE)
if spec is None or spec.loader is None:
    raise RuntimeError("Phase 14 stage tool unavailable")
stage_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(stage_module)

ARCH = "Windows-32bit"
CHECKOUT = "b" * 40
HEAD = "a" * 40


class Phase14DistributionTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        root = Path(tmp.name)
        self.source = root / "artifacts"
        self.dist = root / "dist"
        self.source.mkdir()
        self.files = []
        for name in (f"AlTamam-Portable-{ARCH}.zip", f"AlTamam-Installer-{ARCH}.exe"):
            content = ("synthetic-only:" + name).encode()
            (self.source / name).write_bytes(content)
            self.files.append({"file": name, "bytes": len(content),
                               "sha256": hashlib.sha256(content).hexdigest()})
        metadata = {
            "commit": CHECKOUT, "sourceHeadSha": HEAD,
            "targetArchitecture": ARCH, "runtimeIdentifier": "win-x86",
            "architectureBitness": 32, "files": self.files,
        }
        (self.source / "BUILD_METADATA.json").write_text(json.dumps(metadata))
        (self.source / "SHA256SUMS.txt").write_text(
            "".join(f'{item["sha256"]}  {item["file"]}\n' for item in self.files)
        )

    def test_staging_preserves_all_verified_bytes_and_provenance(self):
        staged = stage_module.stage(self.source, self.dist, ARCH, CHECKOUT, HEAD)
        self.assertEqual(len(staged), 4)
        self.assertEqual({p.parent.name for p in staged}, {"portable", "installer", "metadata"})
        for dest in staged:
            self.assertEqual(dest.read_bytes(), (self.source / dest.name).read_bytes())
        self.assertEqual(self.files[0]["sha256"],
                         hashlib.sha256(staged[0].read_bytes()).hexdigest())

    def test_tamper_or_missing_installer_or_wrong_sha_never_stages(self):
        with self.assertRaises(ValueError):
            stage_module.stage(self.source, self.dist, ARCH, CHECKOUT, "c" * 40)
        (self.source / self.files[1]["file"]).write_bytes(b"tampered")
        with self.assertRaises(ValueError):
            stage_module.stage(self.source, self.dist, ARCH, CHECKOUT, HEAD)
        self.assertFalse((self.dist / "installer" / self.files[1]["file"]).exists())

    def test_staging_rejects_architecture_mismatch_and_incomplete_metadata(self):
        with self.assertRaises(ValueError):
            stage_module.stage(self.source, self.dist, "Windows-64bit", CHECKOUT, HEAD)
        (self.source / "SHA256SUMS.txt").unlink()
        with self.assertRaises(ValueError):
            stage_module.stage(self.source, self.dist, ARCH, CHECKOUT, HEAD)


if __name__ == "__main__":
    unittest.main()
