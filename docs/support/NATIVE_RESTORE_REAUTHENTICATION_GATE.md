# Native restore reauthentication prerequisite — safe staging only

This batch adds `NativeRestoreReauthenticationGate` in
`src/AlTamam.Application/Bridge/` as a **native-host-only** prerequisite.
The general administration user's *existing* active session is insufficient
by itself; the real password is freshly verified against the same Argon2id
credential and SQLCipher database used at login. A forged role, expired or
revoked session and wrong password are denied. Five failed reauthentication
attempts feed the existing failed-login audit table and 15-minute account
lockout, never a second or separate password cache.

The candidate SQLCipher backup is re-inspected under the already-protected
original encryption key and bound to its complete SHA-256. A native-only
opaque approval is stored **in memory** for at most 60 seconds, is not
transmitted to JavaScript, and is burned before its first consumption.
Consumption requires the same still-active admin session, unchanged encrypted
candidate bytes, and hands the candidate to the existing non-destructive
`EncryptedRestoreStagingService`. That service preserves a WAL-consistent
encrypted current-database rescue and never swaps the live database. Invalid
or stale approvals cannot stage a backup; after-session revocation or a
candidate change also fails closed.

The new `NativeRestoreReauthenticationGateTests` use only separate
synthetic SQLCipher databases and real Argon2id authentication to cover
authorized staging, denied credentials, forged roles, unknown SHA-256,
revocation, expiration, persistent failure count and changed selected bytes.

## Intentionally not activated

**This class is not attached to the WebView2 action manifest or to the native
MainWindow restore dialog.** It does not enable
`RestoreDatabaseAtomically` or any destructive action, and is not a claim
that production restoration is ready. Remaining mandatory prerequisites:

1. A real native Windows GeneralAdministration password dialog and explicit
   warning/confirmation bound to the inspected SHA-256, never a JS-only prompt.
2. Native quiescence: stop automatic attendance / backup schedulers, cancel
   in-flight bridge operations, revoke or suspend the active session, dispose
   WebView2 gateway and every SQLCipher connection; retain the single-process
   mutex throughout the critical cutover.
3. The separately tested `OfflineRestoreSwitch` invoked **only offline**
   using an approved, still-verified stage; ensure durable rescue/rollback and
   startup journal reconciliation, without introducing new storage directories.
4. On-disk power-cut, disk-full, rollback, password recovery and second-PC
   acceptance on real independent Windows computers.
5. Explicit owner go/no-go on final release after the exact frozen SHA and
   independent verification. The clean-machine acceptance workflow is still
   skipped in ordinary PR/main builds.

This batch changes no person identity, daily/monthly attendance rule, data
schema or immutable historical archive. Portable and Installer continue using
the same `%LOCALAPPDATA%\\AlTamam` folder without migration.
