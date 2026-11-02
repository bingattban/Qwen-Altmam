#!/usr/bin/env python3
"""Phase 14: stage EXACT verified release bytes under dist/portable and
dist/installer without changing the Package/Release tested SHA.

The real Windows packages are created by the Release matrix AFTER all tests.
This script cannot turn synthetic inputs into authentic installers: it merely
verifies their source provenance and streams identical bytes into final paths.
"""
from __future__ import annotations

import importlib.util
import json
import os
import re
import shutil
import sys
from pathlib import Path


def _load_verifier():
    module_path = Path(__file__).with_name("verify-release-artifacts.py")
    spec = importlib.util.spec_from_file_location("altamam_phase14_verifier", module_path)
    if spec is None or spec.loader is None:
        raise RuntimeError("Release artifact verifier unavailable")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def stage(artifacts: Path, distribution: Path, arch: str,
          checkout_sha: str, source_head_sha: str, version: str) -> list[Path]:
    # Internal package names remain unchanged through the real Windows smoke
    # test; ONLY already verified bytes are staged under owner-approved names.
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._+-]{0,80}", version):
        raise ValueError("Invalid customer release version")
    verifier = _load_verifier()
    errors = verifier.verify_artifacts(artifacts, arch, checkout_sha, source_head_sha)
    if errors:
        raise ValueError("Phase 14 rejected source artifacts: " + "; ".join(errors))
    metadata = json.loads((artifacts / "BUILD_METADATA.json").read_text(encoding="utf-8"))
    if metadata.get("version") != version:
        raise ValueError("Stale or inconsistent customer release version")
    runtime = "win-x86" if arch == "Windows-32bit" else "win-x64"
    pairs = [
        ("portable", f"AlTamam-Portable-{arch}.zip",
         f"AlTamamDaily_{version}_Portable_{runtime}.zip"),
        ("installer", f"AlTamam-Installer-{arch}.exe",
         f"AlTamamDaily_{version}_Setup_{runtime}.exe"),
    ]
    original_records = {row["file"]: row for row in metadata["files"]}
    result: list[Path] = []
    rewritten_records = []
    for kind, old_name, new_name in pairs:
        origin = artifacts / old_name
        destination = distribution / kind / new_name
        destination.parent.mkdir(parents=True, exist_ok=True)
        pending = destination.with_name(destination.name + ".partial")
        try:
            with origin.open("rb") as source, pending.open("xb") as target:
                shutil.copyfileobj(source, target, length=1024 * 1024)
                target.flush()
                os.fsync(target.fileno())
            declared = original_records[old_name]
            if (pending.stat().st_size != declared["bytes"] or
                    verifier.hash_file(pending) != declared["sha256"]):
                raise ValueError("Staged customer package changed bytes: " + new_name)
            os.replace(pending, destination)
            result.append(destination)
            rewritten_records.append({**declared, "file": new_name})
        finally:
            pending.unlink(missing_ok=True)

    # Customer manifest must describe the ACTUAL four named download files,
    # not the short-lived build intermediates. Hashes are for real file bytes.
    revised = {**metadata, "files": rewritten_records}
    text_files = {
        "SHA256SUMS.txt": "".join(
            f'{row["sha256"]}  {row["file"]}\n' for row in rewritten_records
        ),
        "BUILD_METADATA.json": json.dumps(revised, ensure_ascii=False, indent=2) + "\n",
    }
    for name, contents in text_files.items():
        destination = distribution / "metadata" / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        pending = destination.with_name(name + ".partial")
        try:
            with pending.open("x", encoding="utf-8", newline="\n") as target:
                target.write(contents)
                target.flush()
                os.fsync(target.fileno())
            os.replace(pending, destination)
            result.append(destination)
        finally:
            pending.unlink(missing_ok=True)
    return result


def main(argv: list[str]) -> int:
    if len(argv) != 7:
        print("usage: stage-phase14-distributions.py artifacts/ dist/ Windows-32bit|Windows-64bit checkout-sha source-head-sha version")
        return 2
    try:
        files = stage(Path(argv[1]), Path(argv[2]), argv[3], argv[4], argv[5], argv[6])
    except (OSError, ValueError, RuntimeError) as exc:
        print("PHASE14 DIST STAGE FAIL:", exc)
        return 1
    print("PHASE14 DIST STAGE PASS: verified standalone customer files:")
    for file in files:
        print(" ", file.as_posix())
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
