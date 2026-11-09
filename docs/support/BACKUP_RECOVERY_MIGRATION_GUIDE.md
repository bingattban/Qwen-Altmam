> **Current implementation checkpoint — 2026-11-09 — NO-GO (historical details below).** A physically triggered **native WPF** backup restoration path now exists (R08/R09) with fresh GeneralAdministration reauthentication, shutdown/drain, encrypted rescue, atomic switch and restart. The WebView2 action `RestoreDatabaseAtomically` remains **disabled**. This corrects the older 'no native workflow exists' text below. It is **not physically accepted on customer hardware**; never use real personnel data without explicit release signoff. For current operator instructions and the precise separate native-vs-browser boundary see [CURRENT_RECOVERY_OPERATOR_GUIDE.md](CURRENT_RECOVERY_OPERATOR_GUIDE.md).

---

# Backup, recovery and migration — offline security boundaries

**Current state: NO-GO for production atomic restore.** Read-only `PreviewRestore` and native encrypted staging/rollback infrastructure do **not** make a customer-accessible atomic restoration workflow. `RestoreDatabaseAtomically` is deliberately unavailable while final native and owner gates remain open.

## Backups
- Live encrypted SQLCipher database, protected key, WebView2 profile and local rescue directory are under the **per-account** `%LOCALAPPDATA%\AlTamam` root. Never place a second 'portable database' beside the EXE.
- Make backups using the application action **CreateVerifiedBackup**, not arbitrary copies of a live WAL-mode DB. Verify backup inspection, stored SHA-256 and encryption integrity; keep an independent copy away from install/uninstall paths.
- The saved export/backup folder may point to drive E:. If disconnected or unwritable, show the error and **reselect** an available writable folder; do not silently redirect to another disk or imply the operation succeeded. The actual physical E: user journey still requires Phase 15 acceptance evidence.
- A test backup must be made with synthetic personnel only. An incomplete or interrupted write must not invalidate the last good backup.

## Preview and staging are not activation
1. Verify the candidate backup, expected SHA-256, schema and original SQLCipher key.
2. `PreviewRestore` compares selected encrypted backup counts to the live DB for authorized administrators without changing live rows. A successful preview is **NOT a restore**.
3. Native-only reauthentication support verifies an admin's current Argon2id password and creates a single-use, 60-second approval for encrypted **staging**. That is a technical prerequisite, not a public UI activation.
4. Staging creates a WAL-consistent encrypted live rescue, checks candidate bytes/key/schema/integrity, and leaves the live DB unchanged. A separate OfflineRestoreSwitch has guarded startup-journal reconciliation and synthetic interruption coverage, but intentionally lacks approved end-user activation.
5. Never rewire the WebView2 action to a destructive switch until every connection/scheduler is quiesced, the native password/confirmation UX exists, crash and disk-full recovery pass physically, and owner explicitly approves native restoration.

## Second-computer migration / recovery acceptance — still NOT EXECUTED
- A Windows DPAPI-protected key tied to one account/device is **not** inherently portable. Do not copy `db.key.protected` to another computer and assume it will decrypt.
- Use only the explicitly supported portable encrypted recovery-key package and its independently verified authorization, never an ad-hoc plaintext key or silent key migration. Owner-approved recovery steps and two-PC testing remain open; do not claim that successful staging on the original machine proves another account/device will work.
- Required synthetic two-machine acceptance: capture original verified SHA and encrypted backup; recover with valid portable recovery material on clean offline Windows under a different Windows account; verify SQLCipher integrity and historical PersonIds; separately show wrong key, changed backup, tampered intent, invalid journal, power cut and disk-full fail closed with preserved rescue.
- Document both PC builds/bitness/accounts (non-sensitive labels only), crypto/key package format, exact source/backup SHA, reproducible operations, errors and hashes before final signoff.

## Recovery / incident handling
- On startup failure, stop launching repeat restores. Keep source DB, `-wal`, `-shm`, protected key, encrypted backups, staging/rollback/rescue and intent journal intact until diagnosis. Preserve original state and collect error messages.
- Do not swap SQLite files while the app is open or bypass SQLCipher, the single-process lock, the native security gates or the authenticated audit trail.
- For an actual clean reinstall or upgrade, test under the SAME Windows account, check `%LOCALAPPDATA%\AlTamam` files and histories before/after. Same-version overwrite is not a genuine upgrade test.

See `docs/support/NATIVE_RESTORE_REAUTHENTICATION_GATE.md`, `RESTORE_STAGING.md`, `OFFLINE_RESTORE_ACTIVATION_CORE.md`, and `docs/release/GO_NO_GO.md`.
