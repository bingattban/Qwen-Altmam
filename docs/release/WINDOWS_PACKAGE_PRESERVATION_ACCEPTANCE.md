# Windows package preservation — automated acceptance subset (15.06)

This script is a **genuine Windows-runner integration test** of the packaged
artifacts, not a mocked installer and not a declaration of completed Phase 15.06
or Phase 16 final clean-machine acceptance.

**Test file:** `build/test-package-appdata-preservation.ps1`

**Execution:** In both Windows-32bit / win-x86 and Windows-64bit / win-x64
independent Release matrix jobs, **after the full test suite and after Inno
Setup compiles the real installer**, before SHA-256 metadata and upload.
The Windows Core and Windows Release jobs must still pass on the same PR head.

## Automated proof, using only synthetic data

1. Expand the actual customer-facing Portable ZIP in a newly created, isolated
   `RUNNER_TEMP` folder. Inspect its PE architecture, bundled SQLCipher,
   WebView2 and offline resources with the existing layout validator.
2. Execute the real Portable WPF EXE `--layout-selfcheck` and check exit code
   with a bounded timeout. No production database or DPAPI key is created,
   moved or migrated by this check.
3. Capture SHA-256 of any pre-existing `%LOCALAPPDATA%\AlTamam\altamam.db`,
   `db.key.protected`, WAL and SHM files, including their *absence* if not
   present. Create one uniquely named synthetic sentinel inside the app's
   existing `backups` directory; no real backup, key, or personal record is
   manufactured or used.
4. Execute the genuine Inno installer silently into a new temporary directory,
   assert it produces the WPF EXE and an uninstaller, check its executable
   SHA-256 **exactly matches Portable**, and run its `--layout-selfcheck`.
   Assert no app-data change or adjacent `data` directory.
5. Run the real uninstaller silently, verify the installed executable is gone,
   and verify AppData and the sentinel are bit-for-bit preserved.
6. Reinstall the *same* installer, verify the executable checksum and layout
   self-check again, and ensure the original AppData/key/sentinel remain
   unchanged. Any nonzero exit, timeout, missing file, or changed hash **fails
   the Windows Release job before any customer-facing artifacts are uploaded**.
7. Remove only the randomly named synthetic sentinel and isolated temporary
   installation/extraction directory. Never delete or rewrite existing user
   `altamam.db`, `db.key.protected`, backups or rescue data.

## What remains NOT RUN / NOT accepted

- The EXE layout self-check is **not** an interactive WebView2 end-to-end test.
  The release workflow separately performs real Windows `--webview-selfcheck`,
  but complete Arabic UI and operational E2E actions still require a verifier.
- Neither first-run founding admin, actual user login, 10k-person UI
  interaction, attendance, external transfer, SQLCipher restoration,
  restore rollback, actual power-loss/disk-full events, nor cross-device key
  recovery is executed by this package-preservation script.
- These jobs use GitHub-hosted Windows runner environments; they are **not
  evidence of completely clean standalone customer devices** or of an
  owner-approved final release. The manual/tagged `clean-windows-acceptance`
  gate remains separate and must not be silently marked green.
- Portable and installed programs use the **same Windows user
  `%LOCALAPPDATA%\AlTamam` data directory** without migration, as explicitly
  approved by the owner. They do not store databases beside the EXE.
- The actual `RestoreDatabaseAtomically` bridge action remains disabled
  pending its native reauthentication, fully quiesced services, power-loss,
  recovery-key and independent Windows acceptance gates.
