# Installation, Portable operation and first use (offline Windows)

**Release status: candidate / NO-GO.** Use only verified TEST packages while final Phase 15 acceptance remains open. Never use production personnel data for release qualification.

## Choose the correct, authentic package
- Windows 32-bit (x86): `AlTamamDaily_<VERSION>_Portable_win-x86.zip` for direct run, or `AlTamamDaily_<VERSION>_Setup_win-x86.exe` for setup.
- Windows 64-bit (x64): `AlTamamDaily_<VERSION>_Portable_win-x64.zip` for direct run, or `AlTamamDaily_<VERSION>_Setup_win-x64.exe` for setup.
- GitHub Actions uploads have run/version suffixes around their artifact **container** names; inspect the archive and checksum metadata for the actual filename.
- Download each class and same-architecture `SHA256SUMS.txt` / `BUILD_METADATA.json` from the **same successful run** as the frozen source SHA. Run PowerShell `Get-FileHash -Algorithm SHA256 <file>` and compare to the actual checksums inside that metadata. Reject missing/changed files or wrong architecture.
- This application is WPF/.NET 10, installed and run entirely locally. Its package contains a signed Microsoft WebView2 **offline** prerequisite installer. On machines lacking the runtime, follow the included local prerequisite instructions; do not assume an online download is permitted.

`<VERSION>` is the exact metadata-verified CI or final version. Four real customer files and their own SHA-256 values must come from a successful same-SHA Windows Release run; GitHub artifact archive labels are not customer filenames. No final version is released before independent offline Windows acceptance and owner approval.

## Portable
1. Extract the **complete ZIP** to a writable local folder; do not run an EXE from the compressed ZIP and do not remove bundled native DLLs, fonts, offline WebView2 files or license notices.
2. Run `AlTamam.DesktopHost.exe` from inside the extracted folder. Normal direct-run does not install the app; an absent system WebView2 runtime can require a separate offline prerequisite installation.
3. Moving the full extracted directory must never move, delete or recreate user data; keep the same Windows account when confirming continuity.

## Installer
1. Verify the Installer EXE and run setup with only the permissions requested by the authentic installer. Start the app from its installed shortcut or executable.
2. Test uninstall/reinstall under the SAME Windows user. An uninstall must preserve the application data in the user profile; do not manually delete it. The earlier runner checks covered *same-version* reinstallation, **not** a true older-to-newer production upgrade.
3. Both Portable and Installer use the SAME per-user data directory: `%LOCALAPPDATA%\AlTamam`. Neither should write a second database beside the Portable EXE. Another Windows account has its own data root.

## First-use founding account
1. With no existing account provisioned, the **التهيئة الأولى** first-run screen creates the sole `GeneralAdministration` founding user. There is no preset administrator/password.
2. Enter a unique username and strong password, confirm the password and explicitly finish first-use setup. The founder's display/full name is **optional**. The founding administrator is an account, not an automatically manufactured personnel record; `LinkedPersonId` remains NULL.
3. Store recovery materials and the chosen credentials securely outside the installation folder. Log out normally, then test a fresh offline login. Ordinary Administration and Supervisor accounts are created later only through authorized account management.
4. A Supervisor must not perform closed-day corrections, audit-display edits/deletes, admin attendance catch-up or final restore actions. A General/Ordinary Admin may perform approved attendance corrections and audit-display overlays under current policy, but this NEVER deletes immutable original audit/history.

## If something fails
- Missing WebView2: verify the genuine bundled offline prerequisite and current Windows suitability; do not bypass app origin/CSP/security checks.
- Missing E: backup drive / invalid configured output folder: stop, use the application's output-directory selection flow, choose an available writable location, and verify the new path before repeating backup/export. No silent fallback or secret data relocation is acceptable.
- Wrong SQLCipher/recovery key, tampered journal or ambiguous restore: DO NOT delete data, journal, key or rescue files. Use the recovery guidance and preserve evidence. Automated restore activation is deliberately unavailable.
- Record OS bitness/build, offline proof, package SHA, exact failure message and log when escalating. This document is not proof that any clean machine was tested.
