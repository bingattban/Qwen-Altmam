# AL-TAMAM DAILY — Phase 15 GO/NO-GO gate
Date of review: 2026-11-02. Repository: bingattban/Qwen-Altmam.
**Decision: NO-GO — not production-ready; owner signoff NOT GIVEN.**

## Exact evidence and scope
- Merged prerequisite: [PR #191](https://github.com/bingattban/Qwen-Altmam/pull/191), head `f65f2d4ca468aaf65d4b8825a93790f380a9db5f`, merged into main at `c599b9aae191be36cf1ef72193719f2ca2182d60`.
- [Core PR run 41372987699](https://github.com/bingattban/Qwen-Altmam/actions/runs/41372987699): completed SUCCESS on PR head; .NET 10 integration 10/10, unit 14/14, main test suite 980/980, no failed/skipped; prepackage real Windows WPF/WebView2 UI probe passed. These are three distinct suites, not 1,004 independently reviewed acceptance scenarios.
- [Release PR run 41372987701](https://github.com/bingattban/Qwen-Altmam/actions/runs/41372987701): completed SUCCESS on the same PR head; tests BEFORE x86/x64 packaging. The four customer-class artifacts and two checksum/metadata artifacts were uploaded; their actual customer-file SHA-256 values are in the downloadable SHA256SUMS.txt; the verified reference values are also transcribed in the addendum below. The GitHub artifact-wrapper digests are DIFFERENT. Do not mistake GitHub's artifact-wrapper digest for a distributed file digest.
- At creation of this document, main push runs 41375807496 and 41375807497 were not yet verified complete; PR-head success is not post-merge/main acceptance. Recheck live outcomes for the freeze SHA.
- Phase 14's installer uninstall/reinstall, relocation, native bridge and data-preservation checks were automated on GitHub-hosted Windows; they are not tests on independent CLEAN OFFLINE Windows machines. The Release job named "Clean-machine acceptance matrix" was SKIPPED on the PR run.

## 15.01–15.10 release-gate matrix
| Requirement | Verified evidence as of review | Final acceptance status |
| --- | --- | --- |
| 15.01 Unused-code pruning without behavior loss | Existing guarded source and tests inspected. No production code deleted without proof of unreachability. Stale PRs #84/#91 were already closed unmerged; not reapplied. | PARTIAL — per-handler parity and owner signoff on any new conflict still required |
| 15.02 All SECTION 07 and live screen_070/screen_025, screen_020 retired | 69-screen manifest: unique screen_070 embedded AddPerson and screen_025 PersonnelReport; no screen_020. JS report-group regression and native bridge tests exist. | PARTIAL — real end-to-end operation of all routes/roles NOT demonstrated |
| 15.03 All roles, founder, audited actor identity | 103-action role manifest, RBAC and encrypted integration suites, founder first-use tests and immutable audit writer inspected. | PARTIAL — exhaustive per-command three-role, historical account-owner display acceptance NOT demonstrated |
| 15.04 Archived same vs different human | ArchivedIdentityReturnIntegrationTests and monthly history/report UI tests present. Same human retains PersonId, new individual requires distinct PersonId. | PARTIAL — comprehensive Windows report/Excel identity separation acceptance NOT demonstrated |
| 15.05 08:00 auto/20:00 separate lock, counters, transfers and history | Phase07CalendarClosureTests and automatic-policy integration, dashboard and monthly regression suites pass in PR CI. | PARTIAL — actual local-clock, multi-day, transfer-and-report operator journey NOT demonstrated |
| 15.06 SQLCipher and cross-PC restore | SQLCipher integration, signed intent validation, preview/staging and native reauth prerequisite tests exist. Real restore activation (`RestoreDatabaseAtomically`) is DISABLED. | **BLOCKED — no live atomic restore, cross-PC acceptance or E: folder-chooser test** |
| 15.07 Four clean-Windows distributions | PR CI published four distinct x86/x64 Portable/Installer artifacts with SHA-256 metadata and automated same-version AppData-preservation smoke. | **BLOCKED — independent clean offline Windows, power loss, real two-version upgrade absent** |
| 15.08 10,000 people × multiple years | 10,000-person search test emits measured runner evidence; separate SQL/multi-month fixture has only 2,000 people × 6 months. | **BLOCKED — specified 10k multi-year attendance benchmark and actual multi-year figures missing** |
| 15.09 Handover documentation | This tracked gate, changelog, support guides, and CI artifact registry are introduced by a separate Phase 15 PR. | PARTIAL — signed owner handover and actual independently accepted binaries missing |
| 15.10 Final GO/NO-GO | Same-SHA automated candidate evidence exists for PR head; production-critical external checks and owner signature absent. | **NO-GO** |

## Blocking acceptance / release procedure
1. Freeze a single immutable release commit SHA. Verify BOTH Windows Core and Release on precisely that SHA; retain run IDs, TRX, Windows E2E logs, artifact BUILD_METADATA.json and SHA256SUMS.txt, sizes and all four customer files. No CI failure or skipped mandatory test.
2. On independent, network-disconnected CLEAN 32-bit and 64-bit Windows hosts, exercise both direct-run Portable and Installer, founder setup, each role and key operation, file export, backups, restart, uninstall/reinstall and genuine older-to-newer upgrade. Record OS build, hardware, offline proof, package checksum, screenshot/log and exact observed results per distribution.
3. Demonstrate a 10,000-person **multi-year** attendance/roster/monthly/report/Excel benchmark with *measured* values and hardware/configuration, output parity, cancellation and no stale cache. Do not extrapolate from the 10k search-only or 2k six-month tests.
4. Independently verify SQLCipher encrypted recovery with synthetic data on a SECOND Windows machine/account, including correct portable key, wrong key, tampered intent, staged crash reconciliation, physical interruption/disk-full, restored identity/history, and E: unavailable folder re-selection. Preview/staging is NOT atomic activation. Do not enable destructive restore without native reauthentication/quiescence and explicit owner approval.
5. Confirm no unresolved P0/P1 issue and document recorded owner signoff on NATIVE RESTORE and FINAL RELEASE. Otherwise retain NO-GO and do not publish production tags/releases.

Owner signoff — native restore: **PENDING**.
Owner signoff — final production release: **PENDING**.
Independent verifier / machine evidence: **NOT SUBMITTED**.

## Addendum: authentic SHA-256 of four customer files, prior PR #191 only

Read directly from GitHub Actions checksum archives **13110143693** (x86) and **13110517916** (x64), run **41372987701**, version `0.0.831-ci`, tested checkout `2505989e7c00e769bba5fb7ce4b57747dcbb732d`, PR source `f65f2d4ca468aaf65d4b8825a93790f380a9db5f`. This is **unsigned CI candidate reference evidence**, NOT a Phase 15 final-release signature or a different-commit test.

| Distribution | Customer file | Actual SHA-256 |
|---|---|---|
| 32-bit Portable | `AlTamam-Portable-Windows-32bit.zip` | `57c072bc13136d1ab1edf4b2e5453dc7620f409638ea5b5e44307053a066cfa0` |
| 32-bit Installer | `AlTamam-Installer-Windows-32bit.exe` | `f08b396b1ec5b8e4e3e82b8ee889c65877dc9fd042536020784bb78b3dcfb67c` |
| 64-bit Portable | `AlTamam-Portable-Windows-64bit.zip` | `152457eb7fd2017ac18c927825d4bbd5d83cea07bc66669d01106d7539b7b89f` |
| 64-bit Installer | `AlTamam-Installer-Windows-64bit.exe` | `d0c518479685e12c5d2cffbee0d332ae713f76d7e8e9618658b20494fe606a75` |

Measured narrowly on Core runner: synthetic **10,000-person search = 2.64 ms**, not 10,000-person multi-year monthly/report/export load. Release runner hardware facts: Windows Server 2025 Datacenter build 26100; AMD EPYC 7763; 2 cores, 4 logical processors, 17,174,360,064 bytes RAM. This is hardware-specific, not a universal speed claim. The Core measurement and Release hardware were recorded in separate run artifacts; do not misrepresent them as one combined benchmark. See `artifacts/REFERENCE_PR191_CANDIDATE.json`.

New Phase 15 Windows Release `handover-evidence` job must independently download **all six** new same-run package/checksum artifacts, rehash the actual **four** customer files, and prove checkout/source/version equality before publishing a candidate-only JSON inventory. Independent clean OFFLINE Windows, native restore/cross-account recovery, older→newer upgrade and owner GO remain **NOT EXECUTED / BLOCKED**.
