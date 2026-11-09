# R17 — Side-effect-free SQLCipher validation after restore activation
Date: 2026-11-09.
Source baseline: R16 merged `61bf6f5c077a946f394f199ed490e3eb5acb9b76`.

## Evidence and narrow bug
R14 established on the Windows runner that even `SqliteOpenMode.ReadOnly` on a WAL-format encrypted SQLite file can create `-wal`/`-shm` sidecars next to its **source database**. The R14 key-import verifier was changed to inspect only an isolated copy. The R08–R11 `OfflineRestoreSwitch.ValidExactAsync` method still called `EncryptedBackupInspector.VerifyAsync` DIRECTLY on the newly switched live DB and sometimes on the only valid original rollback. This could create SQLCipher sidecars during a supposedly offline native post-switch or pending-journal startup verification. Such sidecars also make a later `RequireOffline(live)` check reject legitimate recovery.

## Test-first
RED commit `fcf8c3bc345541fdf4f414c8597eef32baa82c2b` adds real disposable SQLCipher tests for a successful native switch and both interrupted startup outcomes (old retained / new verified), requiring exact live DB SHA-256 preservation and zero live `-wal/-shm/-journal`, no rollback sidecars, no stray copy, and idempotent journal reconciliation. A host's writable DB connection must not be opened before these assertions.

Minimal correction: `ValidExactAsync` obtains a non-writable, shared-read lock on the source, verifies exact SHA-256, copies the encrypted source to a unique write-through `.altamam-restore-reconcile-<guid>.dbbackup` scratch in the same volume, and invokes the EXISTING SQLCipher integrity/schema inspector **only on that scratch image**. It independently matches the scratch SHA to the expected digest, and deletes only its own scratch and `-wal/-shm/-journal` sidecars after closing SQLite. On crash this existing scratch naming convention is already recognized by the R13/R15 lost-DPAPI-key guard. If scratch copy fails (for example on disk-full), native recovery fails closed without modifying the customer's original live DB/rollback, and retains the durable intent.

The old unused `HashAsync` was removed. No SQLCipher key format, DB schema, sessions, 69 routes, 103 actions, permission manifests, audit history, or delivery architecture changes.

## Compatibility and test limits
This verification now requires **temporary free disk space at least equal to the encrypted image size** (and modest SQLite sidecar overhead). Insufficient free space must not be bypassed by inspecting the original live/rollback image. Windows-hosted SQLCipher integration and deterministic synthetic interruption are not a physical power cut or disk-full test; original production **NO-GO** and independent clean offline x86/x64, two-device recovery, UI/Excel 10k and owner signoff remain open.

## Merge
Run Windows Core and Windows Release concurrently on an identical PR HEAD SHA. Release tests before packaging. Both packaging matrix jobs must upload x86 and x64 Portable and Installer, and the independent four-package SHA-256 audit must be green. Do NOT merge while any required check is red, missing, or pending. The main-branch post-merge Release 42670076098 initially failed **outside recorded successful preflight and test jobs** without launching the packaging matrix; a rerun of the test job was requested, but its success is not assumed until confirmed. Do not hide this operational discrepancy.
