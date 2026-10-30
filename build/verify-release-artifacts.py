#!/usr/bin/env python3
"""Phase 15 / 05.12: verify current tested checkout and BOTH actual Windows package hashes."""
from __future__ import annotations

import hashlib
import json
import re
import sys
from pathlib import Path

SHA = re.compile(r"[0-9a-f]{40}\Z")
DIGEST = re.compile(r"[0-9a-f]{64}\Z")


def hash_file(path: Path) -> str:
    sha = hashlib.sha256()
    with path.open("rb") as file:
        for chunk in iter(lambda: file.read(1024 * 1024), b""):
            sha.update(chunk)
    return sha.hexdigest()


def verify_artifacts(directory: Path, architecture: str, checkout_sha: str,
                     source_head_sha: str) -> list[str]:
    """Return failures; a stale or corrupted artifact must never be accepted."""
    errors: list[str] = []
    if architecture not in {"Windows-32bit", "Windows-64bit"}:
        return ["Unsupported architecture"]
    if not SHA.fullmatch(checkout_sha) or not SHA.fullmatch(source_head_sha):
        return ["Checkout/source head must each be exact 40-character Git SHA"]
    wanted = [f"AlTamam-Portable-{architecture}.zip",
              f"AlTamam-Installer-{architecture}.exe"]
    sum_path = directory / "SHA256SUMS.txt"
    metadata_path = directory / "BUILD_METADATA.json"
    try:
        raw_sums = sum_path.read_text(encoding="utf-8")
        metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, ValueError) as error:
        return [f"Missing or malformed checksums/metadata: {error}"]
    if not isinstance(metadata, dict):
        return ["BUILD_METADATA.json must be an object"]
    if metadata.get("commit") != checkout_sha:
        errors.append("Checkout commit does not match tested GitHub SHA")
    if metadata.get("sourceHeadSha") != source_head_sha:
        errors.append("Source PR head SHA does not match tested branch head")
    if metadata.get("targetArchitecture") != architecture:
        errors.append("Architecture mismatch in build metadata")
    runtime = "win-x86" if architecture == "Windows-32bit" else "win-x64"
    if metadata.get("runtimeIdentifier") != runtime:
        errors.append("Runtime identifier mismatch")
    if metadata.get("architectureBitness") != (32 if architecture == "Windows-32bit" else 64):
        errors.append("Bitness mismatch")
    files = metadata.get("files")
    if not isinstance(files, list) or len(files) != 2 or any(not isinstance(f, dict) for f in files):
        return errors + ["Metadata must describe exactly two Windows packages"]
    by_name = {f.get("file"): f for f in files if isinstance(f.get("file"), str)}
    if len(by_name) != 2 or set(by_name) != set(wanted):
        errors.append("Metadata package inventory is missing, duplicated or unexpected")
    sums: dict[str, str] = {}
    lines = raw_sums.splitlines()
    if len(lines) != 2:
        errors.append("SHA256SUMS must contain exactly two lines")
    for line in lines:
        match = re.fullmatch(r"([0-9a-f]{64})  (AlTamam-(?:Portable|Installer)-Windows-(?:32|64)bit\.(?:zip|exe))", line)
        if not match:
            errors.append("Malformed SHA256SUMS record")
            continue
        digest, name = match.groups()
        if name in sums:
            errors.append("Duplicated checksum entry")
        sums[name] = digest
    if set(sums) != set(wanted):
        errors.append("Checksum inventory is missing or unexpected")
    for name in wanted:
        path = directory / name
        if path.is_symlink() or not path.is_file():
            errors.append("Missing or linked package: " + name)
            continue
        size = path.stat().st_size
        if not size:
            errors.append("Empty package: " + name)
        measured = hash_file(path)
        declared = by_name.get(name)
        if declared is None:
            continue
        if declared.get("sha256") != measured or not DIGEST.fullmatch(str(declared.get("sha256"))):
            errors.append("Metadata SHA-256 mismatch: " + name)
        if sums.get(name) != measured:
            errors.append("SHA256SUMS mismatch: " + name)
        if declared.get("bytes") != size:
            errors.append("Metadata byte size mismatch: " + name)
    return errors


def main(argv: list[str]) -> int:
    if len(argv) != 5:
        print("usage: verify-release-artifacts.py <artifacts-dir> <Windows-32bit|Windows-64bit> <checkout-sha> <source-head-sha>")
        return 2
    errors = verify_artifacts(Path(argv[1]), argv[2], argv[3], argv[4])
    if errors:
        print("RELEASE PROVENANCE FAIL:\n" + "\n".join(errors))
        return 1
    print(f"RELEASE PROVENANCE PASS: {argv[2]}, both package SHA-256, exact checkout {argv[3]} and source head {argv[4]}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
