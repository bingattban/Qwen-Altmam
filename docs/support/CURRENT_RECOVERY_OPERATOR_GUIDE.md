# Current recovery operator guide — native Windows and release safety
Snapshot: 2026-11-09, following merged R15 PR #266 / main commit
`0e1021fa29438f34c61df2db57cf01b7fae534ce`.

**Status: NO-GO — not production-ready.** This guide describes code that exists
and explains how it may be evaluated on a DISPOSABLE synthetic-data Windows
profile only. It does NOT grant owner signoff for actual personnel databases.

## 1. Separate the two restore entry points

The actual trusted Windows WPF **ملف** menu contains:
- **استعادة نسخة احتياطية بأمان** (`OnNativeRestoreMenuClick`):
  physical native backup selection plus fresh `GeneralAdministration`
  credential verification, protected stage/rescue, explicit confirmation,
  quiescence of the WebView2 bridge and all schedulers, native encrypted
  database switch, and mandatory restart. This implementation exists and has
  automated Windows/SQLCipher tests; it is NOT physically accepted for real data.
- **تصدير مفتاح الاسترداد المشفر**
  (`OnNativeRecoveryKeyExportClick`): native GeneralAdministration-only
  export of the *original* SQLCipher key in a separate, password-wrapped
  `.altrecovery` package. The operator must enter the CURRENT account
  password, and a DIFFERENT 16+ character recovery passphrase twice.
  The native save dialog requires a NEW local file and never overwrites it.

The WebView2/JavaScript action `RestoreDatabaseAtomically` is STILL DISABLED.
This is deliberate and does NOT mean the separate native WPF physical
restoration entry is absent. Never add a JS invocation that bypasses the native
role, password, user confirmation or shutdown gates.

## 2. Safe test preparation (synthetic disposable Windows account ONLY)

1. Use the exact four x86/x64 Portable/Installer artifacts from one SUCCESS
   Windows Release run on the same source SHA as a SUCCESS Windows Core run.
   Verify real customer-file SHA-256 from `SHA256SUMS.txt`, not the GitHub
   artifact-container ZIP digest.
2. Run the relevant x86 or x64 package on a disposable Windows installation
   and use only fabricated personnel. Keep machine-level evidence, build
   number, source SHA, and test-user identity labels with NO personal secrets.
3. In the app, sign in with a real, active `GeneralAdministration` account
   created for the synthetic fixture.
4. Generate a real SQLCipher `.dbbackup` using the program's verified backup
   workflow. Do not copy an active WAL database directly, rename a plaintext
   SQLite file, remove sidecars, or alter a live production database.
5. Test only the physical native WPF actions above. Keep copies of encrypted
   backup and the separately protected `.altrecovery` key **in different
   safe locations**. Store the recovery passphrase separately from both.
6. When testing native restore, inspect the selected backup's provenance and
   fingerprint, enter the current GeneralAdmin password freshly, review
   the explicit final warning, and require restart and fresh login afterwards.
   Record original/new SHA-256 and verified rescue/rollback state.

A successful backup preview, a key export, or a green synthetic CI integration
test does NOT prove physical atomic restore or full operator acceptance.

## 3. Missing DPAPI key or unexpected recovery files — fail closed

The SQLCipher database key is protected by per-Windows-user DPAPI; copying
`db.key.protected` alone to another PC/account does not restore identity.
Existing encrypted databases, WAL/SHM/journal, backups, durable recovery
intents, original rollback, staged/replay images, .altrecovery files and
orphaned preflight or pending DPAPI-key files are evidence: **never delete**
or silently overwrite these while diagnosing startup failure.

The `WindowsDatabaseKeyStore.RecoverExistingDatabaseKey` infrastructure can
re-protect a known original key from a password-protected package ONLY when
it verifies the existing encrypted target database's exact SQLCipher
integrity and current compatible schema using an isolated copy. It refuses
an existing DPAPI key and never replaces the database.

**There is no pre-login key-import UI.** In particular, a missing DPAPI key
prevents opening the SQLCipher Users table, so it is impossible to demand an
existing logged-in GeneralAdmin session for that pre-login flow. A proposed
ceremony for proof of possession/authorization and handling missing keys
requires a separate owner-approved threat model before exposure in the UI.
Do not fabricate a role/session or bypass authentication to make it work.

## 4. Independent release acceptance still NOT EXECUTED

The clean offline Windows matrix has NOT EXECUTED the required cases on an
independent second Windows machine/account, including x86 Portable/Installer,
x64 Portable/Installer, real upgrade across versions, keyboard/RTL, DB
history/PersonId parity, native restore, portable key transfer, a genuine
power interruption, disk-full recovery and full 10k person GUI/Excel loads.
Physical tests require an independent verifier and exact package SHAs,
timestamps, Windows build, reproducible logs and **owner signoff**.

The automated `Clean-machine acceptance matrix (15.06)` job is SKIPPED
in normal PR/main CI and MUST NOT be reported as a green manual result.
The Windows-runner SQLCipher fault injection tests are synthetic, not
physical power-loss evidence.

For the execution matrix and audit fields see:
- `docs/release/PHASE15_ACCEPTANCE_MATRIX.md`
- `docs/support/SECTION11_CLEAN_OFFLINE_WINDOWS_SMOKE.md`
- `docs/support/FINAL_VERIFICATION_CHECKLIST.md`
- `docs/release/GO_NO_GO.md`

Current decision remains **NO-GO**, even after successful automated Windows
Core + Release and four-artifact SHA-256 checks. Do not issue production tags
or apply destructive restoration to actual personnel data before
independent hardware evidence and explicit written owner approval.
