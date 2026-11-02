#!/usr/bin/env python3
"""Approved SECTION 11.17 customer filename contract (single source of truth).

GitHub Actions artifact *container* labels may differ. Only actual Windows
ZIP/EXE customer files must match these four names, as must SHA256SUMS,
BUILD_METADATA and the independent four-package verifier. Never synthesize a
checksum or accept a path from unchecked version text.
"""
from __future__ import annotations

import re

ARCH_TO_RUNTIME = {
    "Windows-32bit": "win-x86",
    "Windows-64bit": "win-x64",
}
VERSION_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9._+-]{0,80}\Z")


def owner_filename(version: str, architecture: str, kind: str) -> str:
    if not isinstance(version, str) or not VERSION_RE.fullmatch(version):
        raise ValueError("Invalid Windows release version")
    if architecture not in ARCH_TO_RUNTIME:
        raise ValueError("Invalid Windows release architecture")
    if kind not in ("Portable", "Installer"):
        raise ValueError("Only approved Portable ZIP and Installer EXE are supported")
    label, ext = ("Portable", "zip") if kind == "Portable" else ("Setup", "exe")
    return f"AlTamamDaily_{version}_{label}_{ARCH_TO_RUNTIME[architecture]}.{ext}"
