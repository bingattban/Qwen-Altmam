# R13 — DPAPI startup fail-closed for orphaned recovery material
Date: 2026-11-09 · Base merge: `02e855229c27205f6a95e56ed991e839e18f6ace` (R12).

## Scope and verified defect
The original Windows key-store created a new 256-bit SQLCipher key when the live DB, WAL and SHM were absent and the local `backups/` directory was empty. An interrupted offline restore can leave only a persistent activation journal, encrypted pre-restore rollback, staged/replay backup or failed-live image in the SAME data directory. These are data-recovery evidence and must block new key generation. An orphaned SQLite `-journal` file and an independent `.altrecovery` package in the same directory also require a fail-closed startup.

## Test-first implementation
- RED test commit `4d7024de42cc43c4535e779bc94a30cf6ac13f2f`: Eight Windows/DPAPI cases each create a different root-level recovery artifact with deliberately NO live DB, original key or backup directory. They demand `CryptographicException(MissingEncryptionKey)`, intact original bytes, and NO new protected key file.
- Positive regression: an actual first-use Windows profile with an empty `backups/` and unrelated `notes.txt` can still create and reopen one DPAPI-protected random key.
- Minimal production fix: `LoadOrCreate` checks SQLite `-journal` as well as WAL/SHM; `ContainsRecoveryArtifacts` checks only named root-level recovery-intent and encrypted recovery/rollback/replay patterns, a failed-live image, `.altrecovery`, and the existing recursive backup folder rule.
- No changes to SQLCipher schema, key file format, password derivation, migrations, 69 routes, 103 actions, native UI, x86/x64 packaging or existing database files.

## Intentionally unresolved
If the ONLY surviving copy of an encrypted backup is in an independent user-selected EXTERNAL drive, detection cannot be guaranteed solely by scanning `%LOCALAPPDATA%\\AlTamam`. A lost original DPAPI key cannot be recreated by generating a new one. A pre-login cross-PC import ceremony remains blocked pending owner proof-of-ownership/approval; NEVER fabricate a GeneralAdmin identity or overwrite the local key/database. Physical disk full, sudden power loss, offline clean-machine trials and user signoff still require independent tests. Do not mark production release GO.

## Delivery
Windows Core and Release run concurrently on identical SHA. Release tests must pass BEFORE building all four Portable/Installer x86/x64 customer artifacts. Independent SHA256 verifies these four customer files. Do not merge with any required failure or missing artifact.
