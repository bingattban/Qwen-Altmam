# R11 — Native SQLCipher recovery I/O failure tests (2026-11-09)

## Baseline
R10 in main: `f8394f3ba2d449268cd3d49ca92baf9dd18833b6`. That change preserved the source rollback on restart by copying it to a write-through, SHA-checked, disposable replay image before performing a same-volume atomic swap.

## Test-first extension
A failing native SQLCipher integration regression was added **before** the production change (RED commit `169ecdbb0affb41ca6cd707c00db8c7e2feb0aa2`). It exercises the actual encrypted DB, journal, rollback and separate rescue in the existing serial `SQLCipherGlobalPool` test collection.

Four deterministic recovery checkpoints are now available only when an explicit test callback is passed:
- `BeforeReplayCopy`: injected disk-full/access refusal before staging.
- `AfterReplayCopy`: injected disk failure after a write-through completed.
- `BeforeRecoveryReplace`: simulated failure after the temporary replay passes full SHA/SQLCipher verification.
- `AfterRecoveryReplace`: simulated interruption after atomic replacement but before journal cleanup.

The tests require unchanged corrupted live image when copy or pre-replace fails, preservation of original rollback SHA and separate emergency rescue SHA, retention of the pending durable journal, no stray replay images, and successful next startup reconciliation. After an interruption *after* replacement, journal must remain and a second startup must recognize and accept the original exact old database, without mixing histories or destroying rescue files.

The only production code modification is a nullable optional test-checkpoint callback with default `null`; the native WPF startup path and live restore path do not pass a callback and execute as before.

## Not yet verified
These test checkpoints simulate named I/O failures in an independently generated encrypted database on the Windows runner. They **do not** prove genuine physical disk-full conditions, sudden power disconnection, file-system atomicity on target customer storage, or cross-machine DPAPI restoration. Those manual tests and user acceptance are still required. No release GO claim is made.

## Required CI
Both Windows Core and Windows Release on identical PR HEAD, tests before x86/x64 Portable and Installer packaging; verify all four delivered file checksums, retain output metadata. No merge if any mandatory automated job fails or artifacts missing. Manual clean-machine matrix remains a separate open production-release gate.
