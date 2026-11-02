#!/usr/bin/env python3
"""Phase 15 candidate audit: independently hash all four real Windows downloads.

The input must be the SIX GitHub Actions artifact directories created by ONE
Windows Release run: Portable, Installer and Checksums for each architecture.
No testing or unsigned candidate can be upgraded to production acceptance by
this verifier. NEVER synthesize package bytes or signatures.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import sys
from pathlib import Path

SHA40 = re.compile(r"[0-9a-f]{40}\Z")
SHA64 = re.compile(r"[0-9a-f]{64}\Z")
VERSION = re.compile(r"[A-Za-z0-9][A-Za-z0-9._+-]{0,80}\Z")
ARCHITECTURES = {
    "Windows-32bit": ("win-x86", 32),
    "Windows-64bit": ("win-x64", 64),
}


def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def audit(root: Path, version: str, checkout: str, source: str) -> dict:
    if not VERSION.fullmatch(version):
        raise ValueError("invalid release version")
    if not SHA40.fullmatch(checkout) or not SHA40.fullmatch(source):
        raise ValueError("checkout and source must be exact lower-case 40-character SHA values")
    if not root.is_dir():
        raise ValueError("downloaded GitHub artifacts directory is absent")
    expected_artifact_dirs = {
        f"AlTamam-{kind}-{arch}-rc-{version}"
        for arch in ARCHITECTURES for kind in ("Portable", "Installer", "Checksums")
    }
    actual_artifact_dirs = {p.name for p in root.iterdir() if p.is_dir()}
    if actual_artifact_dirs != expected_artifact_dirs:
        raise ValueError(
            "artifact count/name mismatch: expected six exact current-run artifact "
            "directories; missing="
            + str(sorted(expected_artifact_dirs - actual_artifact_dirs))
            + "; unexpected=" + str(sorted(actual_artifact_dirs - expected_artifact_dirs))
        )
    results = []
    for arch, (runtime, bits) in ARCHITECTURES.items():
        sumdir = root / f"AlTamam-Checksums-{arch}-rc-{version}"
        sum_path = sumdir / "SHA256SUMS.txt"
        metadata_path = sumdir / "BUILD_METADATA.json"
        metadata = json.loads(metadata_path.read_text(encoding="utf-8-sig"))
        if not isinstance(metadata, dict):
            raise ValueError(arch + ": metadata is not an object")
        for key, expected in (
            ("commit", checkout),
            ("sourceHeadSha", source),
            ("version", version),
            ("targetArchitecture", arch),
            ("runtimeIdentifier", runtime),
            ("architectureBitness", bits),
        ):
            if metadata.get(key) != expected:
                raise ValueError(arch + ": inaccurate source provenance field " + key)
        records = metadata.get("files")
        if not isinstance(records, list) or len(records) != 2:
            raise ValueError(arch + ": expected exactly two metadata file entries")
        listed = {}
        for line in sum_path.read_text(encoding="utf-8-sig").splitlines():
            match = re.fullmatch(r"([0-9a-f]{64})  ([A-Za-z0-9.-]+)", line)
            if match is None or match.group(2) in listed:
                raise ValueError(arch + ": malformed or duplicate SHA256SUMS entry")
            listed[match.group(2)] = match.group(1)
        expected_files = {
            f"AlTamamDaily_{version}_Portable_{runtime}.zip",
            f"AlTamamDaily_{version}_Setup_{runtime}.exe",
        }
        if set(listed) != expected_files:
            raise ValueError(arch + ": checksum manifest does not name both exact packages")
        by_name = {}
        for item in records:
            if not isinstance(item, dict) or not isinstance(item.get("file"), str):
                raise ValueError(arch + ": malformed metadata file entry")
            if item["file"] in by_name:
                raise ValueError(arch + ": duplicate metadata entry")
            by_name[item["file"]] = item
        if set(by_name) != expected_files:
            raise ValueError(arch + ": package metadata names mismatch")
        for kind, suffix in (("Portable", ".zip"), ("Installer", ".exe")):
            name = f"AlTamamDaily_{version}_{'Portable' if kind == 'Portable' else 'Setup'}_{runtime}{suffix}"
            p = root / f"AlTamam-{kind}-{arch}-rc-{version}" / name
            if p.is_symlink() or not p.is_file() or p.stat().st_size <= 0:
                raise ValueError("missing / linked / empty current-run customer package: " + name)
            measured = digest(p)
            declared = by_name[name]
            if (
                not SHA64.fullmatch(str(declared.get("sha256", "")))
                or measured != declared["sha256"]
                or measured != listed[name]
                or declared.get("bytes") != p.stat().st_size
            ):
                raise ValueError("SHA-256 / file byte size mismatch: " + name)
            results.append({
                "architecture": arch,
                "class": kind,
                "file": name,
                "bytes": p.stat().st_size,
                "sha256": measured,
                "githubArtifactName": p.parent.name,
                "runtimeIdentifier": runtime,
            })
    if len(results) != 4 or len({r["file"] for r in results}) != 4:
        raise ValueError("must verify exactly four independent Windows packages")
    return {
        "schema": "AlTamam.Phase15.CandidateInventory.v1",
        "status": "CI_CANDIDATE_ONLY_NOT_PRODUCTION_ACCEPTANCE",
        "testedCheckoutSha": checkout,
        "sourceHeadSha": source,
        "version": version,
        "githubRunId": os.environ.get("GITHUB_RUN_ID", "NOT_EXECUTED"),
        "releaseFiles": results,
        "releasePackageCount": 4,
        "checksPassed": "four file bytes, exact per-architecture SHA256SUMS and metadata, same checkout/source/version",
        "independentCleanOfflineWindowsAcceptance": "NOT_EXECUTED",
        "differentMachineEncryptedRecovery": "NOT_EXECUTED",
        "realPowerFailureRecovery": "NOT_EXECUTED",
        "ownerSignedFinalRelease": False,
    }


def main(args: list[str]) -> int:
    if len(args) != 6:
        print("usage: verify-phase15-handover.py DOWNLOAD_ROOT VERSION CHECKOUT_SHA SOURCE_SHA OUTPUT_JSON")
        return 2
    try:
        output = audit(Path(args[1]), args[2], args[3], args[4])
        target = Path(args[5])
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(output, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print("PHASE15 CANDIDATE VERIFIED: four genuine package SHA-256 values on tested checkout " + args[3])
        return 0
    except (ValueError, OSError, json.JSONDecodeError) as exc:
        print("PHASE15 HANDOVER FAIL:", exc)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
