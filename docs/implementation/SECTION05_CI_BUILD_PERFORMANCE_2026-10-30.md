# SECTION 05 / Phase 01 — Windows CI build time optimization (2026-10-30)

## Approved scope and verified baseline

This change implements the owner's SECTION 05.01–05.05 **inside the existing Phase 01 GitHub CI and Phases 13–15 Windows testing/release jobs**; it adds no implementation phase, business policy or permission change.

Baseline PR #141 at exact head `3383b4ce242a3168e5f772611b6b47ed3996233c`:
- Windows Core [run 41086170240](https://github.com/bingattban2/Qwen-Altmam/actions/runs/41086170240): successful; wall time **8m46s** (16:02:05–16:10:51 UTC).
- Windows Release [run 41086170216](https://github.com/bingattban2/Qwen-Altmam/actions/runs/41086170216): successful; wall time **12m58s** (16:02:05–16:15:03 UTC); preflight ~1m15s; full .NET tests/build ~7m46s; subsequent x86 and x64 packaging run **in parallel**, finishing ~3m46s after the tests. Both genuine Portable and Installer artifacts and SHA-256 evidence were present for both architectures.
- Most time is spent in the full native test suite and two Windows runner startup/restore/compile cycles, not in pure JavaScript validation. Tests, idempotency, RBAC and cryptographic package integrity must never be bypassed for speed.

## Concrete compatible optimizations

| Area | Before | After (candidate) | Guard / tradeoff |
| --- | --- | --- | --- |
| Core (.github/workflows/windows-build.yml) | `compile-core` (fresh runner: restore + compile), then `core-tests` (another fresh runner: restore + implicit test compile), then re-run 10k diagnostics with implicit build | Single `core-tests` Windows runner after manifest preflight: restore once, build the **entire solution including the test project** once, run the **full suite with --no-build --no-restore**, then the original 10k hardware benchmark with those same validated binaries | Keep original `core-tests` job ID and logs, add nonzero TRX test count; remove redundant `compile-core` job; do not alter which C# tests are executed. Existing branch protection or external required-check configuration, if any, must be reviewed for the retired compile-core context. |
| NuGet | Core jobs and Release preflight did not use `setup-dotnet` global package caching | `cache: true` with `cache-dependency-path: Directory.Packages.props` in Core and Release preflight; Release build/test and both packaging jobs already had it | Cache immutable dependency downloads, **not compiled code, business data or test results**. Every job still executes its own required restore/security check. |
| Release artifacts | `upload-artifact@v4` default deflate compression re-compressed already compressed ZIP and EXE files | `compression-level: 0` for Portable ZIP and Inno Setup EXE upload; `compression-level: 1` for retained uncompressed raw publish directories | Keep four final assets, metadata, SHA-256, WebView2 Microsoft signature verification, offline installer, package-install tests and acceptance evidence. Upload bytes may increase for raw dir; compare timing before further tuning. |
| CI safety | Guard checks only immediate exit status for JS scripts | New test-first `build/test-ci-performance-contract.py` runs in **BOTH** existing preflights; it checks Core build reuse/full suite/count, cache, Release test-before-packaging and architecture/artifact expectations, and fast ZIP/EXE uploads, with deliberate negative self-tests | Preflight fails closed on drift. It complements, never replaces, `build/test-ci-exit-guards.py`. |
| Governance wording | Two historical workflow comments referred to unapproved Phase 16 | Refer to existing Phase 15 owner acceptance instead | No history or production policy alteration. |

## Security/reliability boundary (SECTION 05.04)

- **Owning layer:** GitHub Actions Phase 01 orchestration / Phase 13–15 release, plus its dedicated CI contract test. No WebView2/DTO/C# domain or SQLCipher schema changes.
- **DTOs / service calls / permissions:** unchanged. No new UI or C# action; existing authorized bridge paths and role guards stay intact.
- **Validation and errors:** CI fails immediately on a failed Python/Node command or missing/zero-count TRX, unsuccessful restore/compilation, or failed package verification. Release package jobs retain `needs: build-test`.
- **Transactions and rollback:** no business state writes; failure rolls back only candidate promotion by blocking merge, and cancels obsolete PR CI via existing concurrency policy.
- **Testing:** existing full C# core and Release suites, all Node regressions, hardware benchmark, artifact signatures, live Windows WebView2 selfcheck and package install/uninstall/reinstall tests continue. The new Python guard tests its own rejection paths.

## Future operating procedure

1. Review wall time **and per-job duration** on the most recent fully green Core/Release head, including first-run cache misses vs warmed runs. Only compare equivalent code/test counts, runner images and test suites.
2. Group related changes and push once per verified batch; avoid a series of intermediate pushes cancelling repeated 13-minute releases. Do local static checks/tests before pushing.
3. Favor caching **immutable NuGet packages** and eliminating repeated work within one runner; never cache published binaries across SHAs, skip a failing test, disable vulnerability/signature checks, or reuse a pass from another commit.
4. For each optimization, write a failing CI contract regression first, run both workflows concurrently, check the exact final PR SHA and all four x86/x64 packages/checksums, then merge and recheck both main workflows.
5. Do not declare a precise percentage improvement until **both** new candidate runs have completed successfully and comparable results have been measured. Clean-machine offline Windows acceptance and signed/public release are separate owner gates.

**Status of this document:** implementation candidate, not evidence that the improved build has finished or that all SECTION 05 standalone screens are complete.
