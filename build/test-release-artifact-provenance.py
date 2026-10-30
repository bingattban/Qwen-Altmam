#!/usr/bin/env python3
"""Tests-first Phase 15 regression: detect malformed/missing/stale/replaced artifacts."""
import importlib.util
import hashlib
import json
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).with_name("verify-release-artifacts.py")
spec = importlib.util.spec_from_file_location("release_verifier", MODULE_PATH)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
HEAD = "a" * 40
CHECKOUT = "b" * 40
ARCH = "Windows-32bit"


class ProvenanceTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.folder = Path(self.tmp.name)
        names = [f"AlTamam-Portable-{ARCH}.zip", f"AlTamam-Installer-{ARCH}.exe"]
        rows = []
        for name in names:
            content = ("synthetic:" + name).encode("ascii")
            (self.folder / name).write_bytes(content)
            rows.append({"file": name, "sha256": hashlib.sha256(content).hexdigest(), "bytes": len(content)})
        self.rows = rows
        self.meta = {"commit": CHECKOUT, "sourceHeadSha": HEAD, "targetArchitecture": ARCH,
                     "runtimeIdentifier": "win-x86", "architectureBitness": 32, "files": rows}
        self.write()

    def write(self):
        (self.folder / "BUILD_METADATA.json").write_text(json.dumps(self.meta), encoding="utf-8")
        (self.folder / "SHA256SUMS.txt").write_text(
            "\n".join(x["sha256"] + "  " + x["file"] for x in self.rows) + "\n", encoding="utf-8")

    def errors(self, checkout=CHECKOUT, head=HEAD, arch=ARCH):
        return module.verify_artifacts(self.folder, arch, checkout, head)

    def test_valid_current_both_packages(self):
        self.assertEqual([], self.errors())

    def test_tampered_installer_detected(self):
        (self.folder / self.rows[1]["file"]).write_bytes(b"substituted executable")
        self.assertTrue(any("SHA-256 mismatch" in e for e in self.errors()))

    def test_tampered_portable_detected(self):
        (self.folder / self.rows[0]["file"]).write_bytes(b"substituted archive")
        self.assertTrue(self.errors())

    def test_wrong_source_head_or_checkout_detected(self):
        self.assertTrue(self.errors(head="c" * 40))
        self.assertTrue(self.errors(checkout="d" * 40))
        self.assertTrue(self.errors(head="not-a-git-sha"))

    def test_wrong_architecture_or_runtime_detected(self):
        self.assertTrue(self.errors(arch="Windows-64bit"))
        self.meta["runtimeIdentifier"] = "win-x64"
        self.write()
        self.assertTrue(any("Runtime" in e for e in self.errors()))

    def test_missing_artifact_or_checksum_detected(self):
        (self.folder / self.rows[0]["file"]).unlink()
        self.assertTrue(any("Missing" in e for e in self.errors()))
        (self.folder / "SHA256SUMS.txt").unlink()
        self.assertTrue(any("Missing or malformed" in e for e in self.errors()))

    def test_bad_metadata_byte_count_detected(self):
        self.rows[0]["bytes"] = 0
        self.write()
        self.assertTrue(any("byte size" in e for e in self.errors()))

    def test_duplicate_checksum_and_wrong_file_inventory_detected(self):
        same = self.rows[0]["sha256"] + "  " + self.rows[0]["file"]
        (self.folder / "SHA256SUMS.txt").write_text(same + "\n" + same + "\n", encoding="utf-8")
        self.assertTrue(any("Duplicated" in e for e in self.errors()))
        self.write()
        self.meta["files"].append(dict(self.rows[0]))
        self.write()
        self.assertTrue(self.errors())

    def test_unrecognized_hash_format_and_invalid_metadata(self):
        self.rows[0]["sha256"] = "z" * 64
        self.write()
        self.assertTrue(self.errors())
        (self.folder / "BUILD_METADATA.json").write_text("{unclosed", encoding="utf-8")
        self.assertTrue(self.errors())


if __name__ == "__main__":
    unittest.main()
