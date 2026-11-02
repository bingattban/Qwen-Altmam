#!/usr/bin/env python3
"""Phase 14: stage EXACT verified release bytes under dist/portable and
dist/installer without changing the Package/Release tested SHA.

The real Windows packages are created by the Release matrix AFTER all tests.
This script cannot turn synthetic inputs into authentic installers: it merely
verifies their source provenance and streams identical bytes into final paths.
"""
from __future__ import annotations

import importlib.util
import os
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
          checkout_sha: str, source_head_sha: str) -> list[Path]:
    verifier = _load_verifier()
    errors = verifier.verify_artifacts(artifacts, arch, checkout_sha, source_head_sha)
    if errors:
        raise ValueError("Phase 14 rejected source artifacts: " + "; ".join(errors))
    names = [
        ("portable", f"AlTamam-Portable-{arch}.zip"),
        ("installer", f"AlTamam-Installer-{arch}.exe"),
        ("metadata", "SHA256SUMS.txt"),
        ("metadata", "BUILD_METADATA.json"),
    ]
    result = []
    for kind, name in names:
        origin = artifacts / name
        destination = distribution / kind / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        pending = destination.with_name(destination.name + ".partial")
        try:
            with origin.open("rb") as source, pending.open("xb") as target:
                shutil.copyfileobj(source, target, length=1024 * 1024)
                target.flush()
                os.fsync(target.fileno())
            if pending.stat().st_size != origin.stat().st_size or (
                verifier.hash_file(pending) != verifier.hash_file(origin)
            ):
                raise ValueError("Phase 14 staged package is not byte-identical: " + name)
            os.replace(pending, destination)
            result.append(destination)
        finally:
            pending.unlink(missing_ok=True)
    return result


def main(argv: list[str]) -> int:
    if len(argv) != 6:
        print("usage: stage-phase14-distributions.py artifacts/ dist/ Windows-32bit|Windows-64bit checkout-sha source-head-sha")
        return 2
    try:
        files = stage(Path(argv[1]), Path(argv[2]), argv[3], argv[4], argv[5])
    except (OSError, ValueError, RuntimeError) as exc:
        print("PHASE14 DIST STAGE FAIL:", exc)
        return 1
    print("PHASE14 DIST STAGE PASS: verified standalone customer files:")
    for file in files:
        print(" ", file.as_posix())
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
