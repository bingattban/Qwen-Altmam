# R10 — Preserve original encrypted rollback on crash reconciliation
Date: 2026-11-09 · Base main merge of R09: `881ffcfb5a5d0e50f3e929ffc349f4e9fc25a61b`

## Verified source defect
In `OfflineRestoreSwitch.ReconcileAtStartupAsync`, when an interrupted activation left a corrupted or missing live database but a valid original rollback, the previous implementation used `File.Replace(state.Rollback, live, ...)`. On Windows `File.Replace` consumes its source. A successful original-database recovery therefore erased the named rollback image, in conflict with the long-standing requirement to preserve original recovery evidence and allow independent forensic verification.

## Test-first correction
- RED: SQLCipher integration test `InterruptedReplacement_ReconcilesOriginalAndPreservesRollbackAndRescue`. A deliberately interrupted `Checkpoint.AfterReplace` (synthetic only) produces a pending durable journal and independent verified rollback/rescue. Two branches simulate (1) a corrupted live SQLCipher database and (2) a missing live database. Both demand original-data restoration, unchanged rollback SHA-256, preservation of the separate rescue, a clean journal, and idempotent next startup. The corrupt-image branch also retains the exact rejected failed-live file.
- MINIMAL FIX: Copy the exact valid rollback into a new same-directory, uniquely named replay file using write-through and flush-to-disk. Verify the replay's SHA and SQLCipher integrity *before* replacement, and atomically replace/move only the replay. The original rollback is never used as the moving `File.Replace` source. Failed copies, disk-full, corrupted replay, or replace errors retain the original image and pending intent; only the disposable replay may be cleaned.
- There is no new UI action, change to immutable personnel history, SQLCipher key, schema, role, account, bridge action or package architecture.

## Distinguish synthetic tests from manual evidence
This code exercises simulated interruption and byte-level corruption with real temporary SQLCipher files; **it does not simulate physical power removal, unstable storage-controller cache, real OS disk exhaustion, or two-computer encrypted key recovery**. Never mark these items complete without independent Windows evidence.

## Remaining independent Windows acceptance — NOT EXECUTED
1. On offline sacrificial x86 and x64 hosts, preserve separate original encrypted database and `.altrecovery` materials (not actual personnel files); verify known original counts, SHA-256, history and Account/PersonId identities.
2. Interrupt an actual native activation after journal fsync but before replace and then after replace using physical process kill/power loss; restart, recover and confirm no mixed old/new rows, both rollback and rescue remain, and the journal clears only on positive verification.
3. Exhaust the disposable disk while creating the replay (never the operator's actual disk). Prove the original rollback, rescue, journal and live images remain byte-identical and no success receipt is issued.
4. Validate a Windows cross-account and cross-PC decryption using the independently held `.altrecovery` secret. Missing/wrong passphrase, existing destination DPAPI key and incompatible SQLCipher schema must be rejected before any overwrite.
5. On clean offline Windows, verify x86/x64 Portable and Installer, real native WPF restore PasswordBox and final explicit confirmation, plus refusal cases (denied roles, expired session, canceled approval, wrong password, mismatched backup catalog).
6. Final owner acceptance, immutable forensics and operational audit policy remain unresolved. **Release disposition stays NO-GO.**

## CI delivery gate
Windows Core and Windows Release must pass on the **same exact commit SHA**; Release runs tests before producing all four builds. Audit all four customer file hashes (not GitHub wrapper hashes). Do not merge if any required job fails or artifacts are missing.
