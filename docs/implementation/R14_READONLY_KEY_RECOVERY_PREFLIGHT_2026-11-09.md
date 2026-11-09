# R14 — Read-only, schema-gated native SQLCipher key recovery

Date: 2026-11-09 · Base main merge: `4183e85d59773c8f5945e4b85908cfc50547eda8` (R13).

## Verified issue
`WindowsDatabaseKeyStore.RecoverExistingDatabaseKey` previously checked an existing encrypted database by constructing `AlTamamDbContext` and calling `HasValidIntegrity()`. The context opens `SqliteOpenMode.ReadWriteCreate` and executes `PRAGMA journal_mode=WAL`, a **write-capable operation** even for an otherwise read-only verification. This contradicts the recovery API's promise not to modify the customer's encrypted backup before installing the original DPAPI key. The old check also approved any integrity-valid SQLCipher DB without checking the supported schema version.

## Test-first fix
- Failing regression written first at `baecbc8e01dc0541cb48bd58ea8c5dfc796bec3e`.
- Case 1: generate a real original-key encrypted SQLCipher backup, export `.altrecovery`, copy that backup to a new synthetic target Windows key location, and call `RecoverExistingDatabaseKey`. Verify the complete original DB SHA-256 remains **byte-for-byte identical**, neither WAL nor SHM appears, and the original key is installed into destination DPAPI.
- Case 2: change only the disposable target database schema version to unsupported 999, preserving valid encryption/integrity. Recovery must fail closed before writing a DPAPI key, and must leave the database byte-identical with no WAL/SHM artifacts.
- Minimal production correction: reuse existing `EncryptedBackupInspector.VerifyAsync` with an independently distinct path as the protected non-candidate sentinel; the inspector uses read-only non-pooled SQLCipher, integrity and foreign-key checks, required tables, and SHA-256. Require `CurrentSchemaVersion` exactly before DPAPI.Protect and atomic new-key writing.
- The public recovery API remains synchronous and still does **not** copy, move, replace or delete database files. Only the original key from an independently encrypted recovery package can be re-protected to the current Windows user. No new browser, background automation, account, schema, migration, or role behavior.

## Remaining scope and owner policies
The test above is a second *DPAPI location* in a Windows runner, NOT an independent computer or user account. A secure pre-login native UI recovery ceremony, owner proof-of-possession policy, real two-device DPAPI cross-account testing, and physical clean-machine/low-disk/power-loss acceptance are not done and remain independent **NO-GO** conditions. Never imply an approval to overwrite an existing key or decrypt real customer data.

## Required delivery
Two workflows: Windows Core and Windows Release concurrently on the **same exact SHA**, each required check green. Full Release tests **before** producing x86 and x64 Portable and Installer; independently verify all four delivered customer-file SHA-256 values. No merge on failed job or missing artifacts; no production release GO without manually documented acceptance.


## CI failure diagnosis and correction (2026-11-09)
The first R14 Windows Core/Release full suites each reported three failing tests. Two old recovery tests opened the target SQLCipher file through writable, pooled fixture connections before invoking a pre-login recovery, leaving WAL/locks and causing an `IOException` instead of reaching wrong-key verification. The new byte-identity test found that even SQLite `ReadOnly` inspection can create a `-wal` file beside a WAL-format **customer target**.

**Actual correction:** before installing any DPAPI key, reject source `-wal/-shm/-journal` sidecars, hold a shared **read-only file lock** on the exact target, copy its encrypted bytes to a uniquely named isolated scratch directory with write-through flush, run existing SQLCipher read-only integrity/schema/foreign-key verification **only on the copy**, compare the locked source SHA-256 with the independently verified copy, and recheck source sidecars. Only then atomically install the original key. Scratch cleanup deletes only its own directory. In particular, the original target never becomes the SQLite connection's DataSource and can never acquire a WAL/SHM file as a side effect of verification.

Updated fixtures validate actual OFFLINE target snapshots rather than generating WAL sidecars through a test helper before testing key import. The wrong-key test now supplies an independently encrypted wrong-key backup and must return `CryptographicException` with no new target DPAPI key or target byte mutation. This is a functional fix, not a suppression of failing assertions, compiler analyzers, or CI gates.

These are synthetic Windows runner tests; a real different-user/different-PC proof of possession and physical interrupted-disk acceptance remain NOT EXECUTED.
