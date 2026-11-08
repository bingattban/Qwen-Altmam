# R08 — Safe restore staged implementation and final acceptance (2026-11-08)

Base `main`: `659f6628154fe1101f257ea00e80c86f17ae2ff0` (PR #258). Work branch:
`fix/restore-native-activation-coordination-20261108`; review PR #259 (DRAFT).
This is an honest execution ledger, **not a production-release approval**.

## Step 1 — Inventory and real native regression (implemented, pending Windows CI)

- Source inspected: `NativeRestoreReauthenticationGate`, encrypted stage/inspection,
  `OfflineRestoreSwitch`, `NativeOfflineOperationDrain`, WPF gateway and schedulers,
  active session authentication, DPAPI and the R01 disabled bridge handler.
- Red test-first commit `cc2c9a18f68e85e481eb45a7619e1c168ff88cc8`, then coordinator
  `d4f636a7ba984099fb07444f8dfbb2dd7f70feef`.
- Additional wrong-key regression RED `a02b8ef81fd41546b691f2931f238e6a732fe057`;
  pre-shutdown key verification GREEN `faa464aa2ad43d62c7d1decaf0712e571b66a053`.
- Actual encrypted SQLCipher integration scenarios require: deny bad GA password,
  deny abandoned native confirmation, deny changed backup and wrong key BEFORE
  host shutdown, no cutover if quiescence fails, and accepted cutover preserving
  both the old database rollback and independent encrypted rescues.
- No test executed locally: this non-Windows execution environment has no .NET
  SDK and no real Windows/DPAPI/WebView2.
- This is native-only application infrastructure; NEVER invoke it from an
  active WebView2 message lease (self-drain deadlock).

## Step 2 — Windows-owned explicit user flow (implementation candidate, CI pending)

- Physical WPF application menu: file selection through Windows file picker
  only after resolving the active GeneralAdministration session from SQLCipher.
- A new WPF `PasswordBox` collects the current password locally; independent
  native final confirmation identifies the encrypted staged candidate, counts
  and SHA256. No credential, key or in-memory grant enters JS/WebView2.
- The application callback stops the WebView2 gateway and *awaits* the automatic
  attendance, backup and monthly-export schedulers before disposing DB services;
  the single-instance mutex remains owned until exit.
- Only then does the existing journaled `OfflineRestoreSwitch` run. Any error
  once shutdown starts closes the app; it must NOT resume the old session or
  discard rescue/rollback. Successful activation requires a fresh application
  start and authentication with the restored database.
- `RestoreDatabaseAtomically` **remains disabled** in the JS/native dispatcher.
  The only proposed activation route is a separate, physically initiated
  native WPF menu guarded by fresh GA reauthentication.
- Native WPF UI needs real Windows automated testing and independent physical
  review before removing the draft label or enabling a production release.

## Step 3 — Destructive-path recovery acceptance (NOT EXECUTED)

Use only disposable synthetic encrypted Windows test profiles:
- Normal cutover, wrong key, wrong password, revoked role/session and missing
  backup; canceled final approval; file changed before switch.
- Disk-full or sharing violations before durable intent, immediately before
  replacement, and after replacement; actual process kill/power loss and
  startup reconciliation; repeated restart cannot silently create a new key.
- Prove the original current data and entire audit chain survive in the
  independently verified rescue/rollback if the selected older backup takes
  over the live DB.
- Capture exact SQLCipher integrity, version, counts, SHA256 and success/fail
  evidence without logging protected personnel or passphrases.
- Separately decide how a restoration event is recorded for audit and operator
  traceability without mutating archived evidence. NO silent audit rewriting.

## Step 4 — Independent cross-account/cross-PC encrypted recovery (NOT EXECUTED)

- Test real x86 and x64 Portable/Installer on separate offline Windows accounts.
- Verify an approved password-protected `.altrecovery` key package recovers
  only the right SQLCipher database, then opens history and stable PersonIds.
- Deny missing/wrong recovery material, tampered backup, incompatible schema,
  and unexpected DPAPI key; never generate a replacement key over existing data.
- Native import/export dialogs for portable key material require their own
  authorization, data-loss prevention and approval review.

## Step 5 — Exact-SHA Windows CI / release gates (PENDING)

- Tests first; Core and Release concurrently for the identical immutable head.
- Full actual native SQLCipher+WPF WebView2 exercises before release packaging.
- Four genuine distributions: x86 Portable ZIP/Installer EXE and x64 Portable
  ZIP/Installer EXE; independent checksum and source-head provenance.
- No merge on any failed/skipped required automated test or missing package.
- Manual clean-machine/physical Windows UAT is not replaced by a green CI run.

## Step 6 — Remaining project-wide tasks (OUTSIDE R08, NOT CLAIMED COMPLETE)

Reconcile R01 and phase 13–15 manual acceptance; verify every main UI consumer
and error path, role and session failure; perform full 10k real GUI/Excel
benchmarks and upgrade/backup preservation. Afterwards, investigate genuinely
unreachable functions and duplicate code before pruning, document legacy
branches and update historical risk/acceptance ledgers. Never rewrite immutable
history, weaken authorization, change Windows architecture or optimize without
same-data output parity and performance measurements.

## Release disposition

**NO-GO until Steps 3–5 have passing independent evidence and explicit
owner final approval**. The draft PR is an implementation candidate only.
Do not interpret either its creation or CI success as permission to use
unverified destructive recovery on real personnel data.
