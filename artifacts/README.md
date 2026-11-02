# Phase 15 release artifact ledger — NO BINARIES ARE CHECKED IN HERE

This folder is a **documentation/placeholder** path required by the handover contract. It intentionally contains **NO invented EXEs, ZIPs, SHA256SUMS, or BUILD_METADATA**. No GitHub Actions artifact wrapper should be mistaken for a source-controlled customer installer.

Actual PR #191 tested run: https://github.com/bingattban/Qwen-Altmam/actions/runs/41372987701
Source PR head: `f65f2d4ca468aaf65d4b8825a93790f380a9db5f`.
Merged main: `c599b9aae191be36cf1ef72193719f2ca2182d60` (must have its OWN validated final freeze run).

Reported GitHub Actions candidate output classes:
- `AlTamam-Portable-Windows-32bit-rc-0.0.831-ci` (Artifact ID 13110288453)
- `AlTamam-Installer-Windows-32bit-rc-0.0.831-ci` (Artifact ID 13109739678)
- `AlTamam-Portable-Windows-64bit-rc-0.0.831-ci` (Artifact ID 13111120026)
- `AlTamam-Installer-Windows-64bit-rc-0.0.831-ci` (Artifact ID 13111100046)
- Separate checksum/metadata: x86 13110143693; x64 13110517916.

Download from that real run, inspect `BUILD_METADATA.json` and **compute actual SHA-256 of the extracted customer files**. These outputs are CI release CANDIDATES, not owner-approved production releases. Full clean OFFLINE Windows and native restore signoff missing; see `docs/release/GO_NO_GO.md`.

The working release pipeline stages verified byte-identical files under `dist/portable/`, `dist/installer/`, `dist/metadata/` ON THE WINDOWS CI RUNNER; those executable bytes are uploaded as artifacts, not automatically committed to this Git repository.
