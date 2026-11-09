# R12 — Native GeneralAdmin-only encrypted recovery-key export (2026-11-09)

Base main: R11 merge `9a1eaa5a9324f169e1a620447832cd5a9b742ddb`.

## Purpose and implementation (test-first)
The original Windows DPAPI key store already supports the separate, AES-256-GCM, PBKDF2-wrapped `.altrecovery` format, but end users had no trusted Windows export UI. Exposing key material over WebView2 would violate the native-secret boundary.

- RED first: real Windows SQLCipher/DPAPI integration tests for authorized export, independent passphrase unwrap, exact live-database immutability, denial of wrong password, revoked/forged roles, same or too-short passphrase and atomic refusal to overwrite existing `.altrecovery` file. Separate Python contract checks the native WPF menu and three true PasswordBoxes.
- A **physical** trusted WPF `ملف → تصدير مفتاح الاسترداد المشفر` menu is the only entry. The operator chooses a NEW local `.altrecovery` path, provides the current GeneralAdministration password, and enters a DIFFERENT password of 16+ characters twice. Cancellation leaves the database and files untouched.
- `NativePortableRecoveryExportService` requires currently active, freshly reauthenticated GeneralAdministration from the actual SQLCipher user session. The existing authoritative native verification transaction and lockout policy are reused; the role/session is checked again before exporting.
- The existing `WindowsDatabaseKeyStore.ExportPortableRecoveryKey` verifies actual SQLCipher integrity and reads the ORIGINAL Windows-user DPAPI key. It encrypts the SQLCipher key under the separately held password, writes through and flushes to a temporary file, and atomically installs a new file without overwrite. No password, raw key, token or exported file bytes go through the WebView2 bridge.
- WPF Closing blocks during export, preserving the same single-instance mutex and native database lifecycle. Restore cannot start simultaneously.
- No changes to SQLCipher database schema, 69 routes, 103 bridge actions, archival/audit records, personnel, attendance, or the Windows x86/x64 distribution model.

## Not in scope — outstanding safety/owner decision
The import/recover UI for another Windows account **cannot** rely on a current GeneralAdministration session if the local DPAPI key is absent and the encrypted database therefore cannot open. The source-code core for `RecoverExistingDatabaseKey` already exists and explicitly prohibits replacing any existing protected key or database, but a secure *pre-login* native recovery ceremony, proof-of-ownership and loss-prevention policy still need explicit owner decisions before a UI is enabled. Do **not** bypass credentials by fabricating a GeneralAdmin session, silently replacing a DPAPI key, or accepting input from the browser.

Real independent cross-PC and cross-account Windows validation, USB storage operation, actual power interruption and disk-full tests and clean-machine offline x86/x64 Portable/Installer UAT remain **NOT EXECUTED**. This branch is not production acceptance.

## CI and release
Both Windows Core and Windows Release must succeed on the exact PR-head SHA; Release runs all tests before packaging four x86/x64 Portable/Installer images and audits actual customer-file SHA-256 values. Do not merge when any required automated gate fails. Do not certify production GO without the separate physical acceptance and explicit owner signoff.
