# R19 — Non-mutating native portable recovery-key export
Date: 2026-11-10. Baseline: merged R18 main commit `cd62a694ce9e1befcf709c99716166e743a21912`.

## Verified code-level gap and boundary
`WindowsDatabaseKeyStore.ExportPortableRecoveryKey` called `AlTamamDbContext.HasValidIntegrity` on the **customer's source database**. The latter opens SQLCipher as `ReadWriteCreate` and unconditionally executes `PRAGMA journal_mode=WAL`. Consequently a logically read-only credential export can modify a closed encrypted database and produce fresh `-wal`/`-shm` source-side files. This path was not covered by the earlier R14 isolated key-**import** or R17 offline **restore** fixes.

## Real test-first regression
The first branch commit `f6127c2246e136163816ef1ca037fa1ea6b6eded` added Windows SQLCipher tests BEFORE editing production code:
1. Create a real encrypted, current-schema standalone image from SQLite's verified backup, record its exact SHA-256, then export its protected key. Require identical bytes, no new source `-wal`/`-shm`/`-journal`, a decryptable original-key package and no leftover scratch directory. This should fail on the pre-fix `ReadWriteCreate` export path.
2. Keep a real SQLCipher writer connection and a newly committed `PRAGMA user_version` update in live WAL, then export. Require that the portable package contains the original key and the newest committed WAL state remains queryable. This ensures the fix does not incorrectly use a raw main-file copy when recent changes remain in WAL.

The tests are concrete Windows DPAPI + SQLCipher integrations, not static string assertions. A test added before production code is not automatically an observed RED execution: Windows workflow results are required for the result.

## Narrow repair
- For an offline encrypted source with **no SQLite sidecar**, hold a shared read-only file lock across copy/verification/export, copy the byte-exact file to a unique `.altamam-key-preflight-<GUID>/candidate.dbbackup` and inspect that copy with the already-existing nonpooled `EncryptedBackupInspector`. Require a current app schema and exact original SHA. Do not open the source with SQLite or change its journaling mode.
- For an **already WAL-active** source, the main `.db` file alone can be stale. Take a transactionally consistent SQLCipher online backup of the live database to that same isolated directory via the existing `CreateVerifiedEncryptedBackupAsync` implementation, including latest committed WAL transactions. Verify only the isolated backup with the inspector; do not assert that a moving live database's main-file SHA equals the snapshot.
- A failure in snapshot/space/inspection never creates or replaces a portable recovery key package. Cleanup touches only the uniquely created scratch directory. If interrupted, R15's startup key guard already recognizes this directory as old-data evidence.
- The active GeneralAdministration WPF reauthentication, session expiry, separate 16+ character passphrase, original DPAPI key, and no-overwrite `.altrecovery` rules remain unchanged. The WebView2 `RestoreDatabaseAtomically` command remains disabled.

## Operational tradeoff and pending acceptance
The exported key alone is **not a backup** and does not copy encrypted personnel records. The source image is never discarded. Export now needs temporary free space approximately the size of the SQLCipher image plus modest sidecars; low disk means a **fail-closed export**, not a fallback to writable source verification. An already-WAL-active database uses its existing transactionally consistent backup path: it may checkpoint live WAL under standard SQLite behavior; do not claim byte-for-byte immutability for an actively changing database. This behavior is distinct from the offline byte-exact test.

## Merge and production status
Windows Core and Windows Release must succeed in parallel on the **same final SHA**. Release runs all tests before packaging, then x86/x64 Portable and Installer must be created and their actual four binary SHA-256 values independently verified. No merge with any failed/skipped required job or missing artifact. The clean offline Windows UAT job is routinely skipped, and real cross-PC recovery, power interruption, low-disk, native dialog UAT and owner signoff are **NOT EXECUTED**. Production status remains **NO-GO**.
