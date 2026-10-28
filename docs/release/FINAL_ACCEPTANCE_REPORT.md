# Current evidence addendum — 2026-10-29 (owner plan, introductory directive)

**Current governing repository:** [`bingattban2/Qwen-Altmam`](https://github.com/bingattban2/Qwen-Altmam).
**Verified `main` commit:** `0290ebbf235741395bea42eb77cd74fd777865db` (merged PR [#108](https://github.com/bingattban2/Qwen-Altmam/pull/108); later than the owner plan's PR #107 reference).
**State:** CI CANDIDATE BUILT AND TESTED; **NOT A PRODUCTION ACCEPTANCE OR OWNER SIGNOFF**.

This addendum records **live, exact-SHA evidence** for the October 28 owner-reconciled master directive. The October 23 report below is retained as an **unaltered historical snapshot**, not the current test/build state. Its statements about zero tests ever having run, packages never having been produced, the former repository name, and absence of a remote are **obsolete**. The current source-of-truth precedence is: latest explicit owner instruction, approved written owner decisions, reconciled plan, then nonconflicting previous concepts. No historical report is evidence of final approval.

## Verified Windows CI at the exact main SHA

- [Windows Core Build, run 40770099467](https://github.com/bingattban2/Qwen-Altmam/actions/runs/40770099467): **success**; manifest/Arabic UI checks, production build, Windows .NET test suite and measured synthetic search diagnostic passed.
- [Windows Release Packages, run 40770099535](https://github.com/bingattban2/Qwen-Altmam/actions/runs/40770099535): **success**; independent preflight, full C# suite **821 passed, 0 failed, 0 skipped** (Windows `net10.0`), then native x86 and x64 packaging. The package jobs depend on the successful Release test job; Core and Release workflows are separately triggered on PR and main.
- The Release run uploaded **four independent executable distribution artifacts** (not mock binaries): `AlTamam-Portable-Windows-32bit-rc-0.0.478-ci` (artifact 12852717376); `AlTamam-Installer-Windows-32bit-rc-0.0.478-ci` (12851254107); `AlTamam-Portable-Windows-64bit-rc-0.0.478-ci` (12851933029); `AlTamam-Installer-Windows-64bit-rc-0.0.478-ci` (12852553652). The same run uploaded separate SHA-256/build-metadata artifacts for both architectures and a full-suite `.trx` result artifact.
- Runner validation included bundled standalone offline WebView2 setup, genuine WebView2 local-UI self-check, native layout/PE/SQLCipher checks, and an automated real Portable/Installer install-uninstall-reinstall **AppData-preservation subset** on both architectures. This does **not** establish a complete offline user journey on separate clean Windows PCs.

## Open acceptance and scope constraints

1. The run's **`Clean-machine acceptance matrix (15.06) — manual evidence gate` was SKIPPED** on the push workflow; independent offline physical/VM E2E validation, real power interruption/disk-full and cross-PC encrypted recovery still require separate reproducible evidence and owner approval.
2. Destructive `RestoreDatabaseAtomically` remains deliberately **DISABLED**. Its native security prerequisites are partial and must not be bypassed. The latest PR #108 adds a bridge stop-and-drain barrier, **not an enabled final restore workflow**.
3. No final signed release or owner authorization is established by these candidate artifacts; GitHub Releases had no published entries at review. Existing terminology in the historical report concerning `Phase 16` predates the October 28 **phases 00–15** directive and must not silently alter current owner-approved scope.
4. The reconciled full master-prompt file referenced by the historical implementation ledger was **not present** in the examined repository tree at this SHA. The owner is sending approved execution parts sequentially; preserve the owner's priority order and flag a *new* unresolved business-policy contradiction rather than inventing a decision.
5. The artifacts above use the existing Release naming pattern. Final handover filenames must be checked against the owner-approved four-variant naming convention, with independently reproducible checksums.

**Disposition:** verification of the introductory implementation/build directive is complete for the **existing** architecture and CI plumbing. **Overall release remains NO-GO**, and this addendum does not change any historical data, source, code, acceptance-test outcomes, or gate status.

---

# AlTamam — FINAL_ACCEPTANCE_REPORT (Phase 16)

**Report generated:** 2026-10-23 (UTC), by the implementation agent in the production repository workspace.
**Status of this document: DRAFT — NOT AN ACCEPTANCE.** Per 16.07 it is the single final report file; per 16.08 and the Phase 16 gate it must be completed and countersigned by an **independent verifier** on real Windows infrastructure before any release tag is created. Every cell below is marked with its true evidence state. **No absent coverage has been translated into PASS** (16.04).

---

## 1. Frozen candidate (16.01)

| Item | Value | State |
|---|---|---|
| Repository | `bingattban2/AlTamam` (production repo, `/workspace` clone) | OK |
| Branch containing Phases 14–16 deliverables | `qwen-code-9b7d27ae-800b-4231-a238-4ffb193126b8` | Two local commits: `06a46a8` (Phase 14–16 batch, 15 files) + bookkeeping commit recording branch tip. Verifier MUST derive FREEZE_SHA from the merged PR head on the remote, not from in-file text. |
| Baseline HEAD before this batch | `db42f4e` ("Merge pull request #4 … phase-08-movements-grid-91837") | Recorded |
| Candidate commit (local) | `06a46a8` "Phase 16: FINAL_ACCEPTANCE_REPORT…" (+ final bookkeeping commit = branch tip; see note above) | Contains all Phase 14/15/16 files (15 files, verified via `git show --stat`) |
| **Freeze SHA** | **TBD — becomes final only after this branch's PR lands on `main`; derive it from the remote merge commit** | ⛔ OPEN ITEM |

**Freeze procedure (to execute once, then no unrelated changes):**
1. Push branch `qwen-code-…26b8` and merge its PR to `main` (squash or straight merge of the two local commits).
2. Record `git rev-parse main` → `FREEZE_SHA` in §1 above and in the acceptance-repo manifest (§4).
3. Tag nothing. All CI runs for acceptance must check out `FREEZE_SHA` explicitly (`actions/checkout@v4` with `ref: FREEZE_SHA`), never a moving branch.

## 2. Independent verification workspace (16.02)

**Not available from inside this sandbox.** The required setup, to be performed by the independent verifier under owner authorization:

- New private repository (e.g. `altamam-acceptance`) outside the production repo, containing only:
  - `ACCEPTANCE_MANIFEST.json`: `{"freeze_sha": "<FREEZE_SHA>", "workflow_run_ids": [...], "artifact_digests": {...}}`
  - Cloned test harness that checks out `FREEZE_SHA` read-only and runs the suites listed in §3 against it.
- Tests MUST target the exact immutable `FREEZE_SHA` and the artifacts downloaded from the workflow run whose head SHA equals `FREEZE_SHA` (hash comparison per 16.05). Any artifact built from a different SHA invalidates the run.

State: **NOT RUN** (infrastructure gap; owner must provision/authorize).

## 3. Requirement → test traceability and status matrix (16.03 / 16.04)

Legend: **PASS** = executed green with retained logs; **FAIL**; **NOT RUN** = test exists but has never executed successfully anywhere; **UNVERIFIED** = claim without executable test.
The execution environment for all rows is the Windows x64 GitHub Actions runner(s) at `FREEZE_SHA`; results columns are filled from the run URLs recorded in §5.

| Req area | Test vehicle (repo path) | Negative paths / concurrency | Status today | On Windows @ FREEZE_SHA |
|---|---|---|---|---|
| Domain rules (IDs, intervals, attendance-day policy, normalization) | `tests/AlTamam.Tests/Persistence/*`, domain unit tests (Phases 01–08) | overlap/backdate cases present | **NOT RUN** (no dotnet in authoring sandbox) | ☐ PASS ☐ FAIL |
| SQLCipher encryption & key handling | `tests/AlTamam.Tests/Security/SecretHygieneAndFailureTests.cs` + persistence suite | wrong-key open, corrupt DB, DPAPI key loss | **NOT RUN** | ☐ PASS ☐ FAIL |
| Auth (Argon2id, login lockout, RBAC policy fn) | `tests/AlTamam.Tests/Bridge/BridgeRbacMatrixTests.cs` (90 actions × roles × dangerous combos, independent of UI visibility) | unauthorized action via direct bridge call | **NOT RUN** | ☐ PASS ☐ FAIL |
| Audit immutability / overlay edits | audit tests (Phase 11 scope) | physical-delete attempt, closed-day write | **NOT RUN** | ☐ PASS ☐ FAIL |
| Temporal / as-of resolution | temporal fixtures `tests/AlTamam.Tests/Fixtures/*` | name/site change vs historical report | **NOT RUN** | ☐ PASS ☐ FAIL |
| Attendance commit/revise flows | attendance suite | transfer-effective-date collision | **NOT RUN** | ☐ PASS ☐ FAIL |
| Scheduler (20:00 carry-forward, startup catch-up) | scheduler tests (Phase 13 scope) | clock skew, offline overnight | **NOT RUN** | ☐ PASS ☐ FAIL |
| Import/export (Excel) | import/export suite | malformed sheet, traversal filenames | **NOT RUN** | ☐ PASS ☐ FAIL |
| Backup/restore | backup suite | restore over live DB, truncated archive | **NOT RUN** | ☐ PASS ☐ FAIL |
| Bridge security (CSP, virtual origin, navigation, host-object exposure, filename traversal, unsafe download) | `tests/AlTamam.Tests/Security/BridgeFuzzPayloadTests.cs` | fuzzed/malformed payloads, hostile origins | **NOT RUN** | ☐ PASS ☐ FAIL |
| SQLite performance indexes + concurrent ops, outputs unchanged | `tests/AlTamam.Tests/Performance/SqliteIndexPerformanceTests.cs` (synthetic large personnel/month periods) | concurrent writers, index-off parity baseline | **NOT RUN** | ☐ PASS ☐ FAIL |
| Package layout / PE authenticity / manifest guard | `build/validate-layout.py`, `build/manifest-guard.py` (runnable on Linux too) | fake-exe detection, CSP drift | script authored; **executed locally: NO** | ☐ PASS ☐ FAIL |
| Clean-machine install/uninstall/upgrade matrix (15.06 carry-over) | `.github/workflows/windows-release.yml` job "Clean-machine acceptance matrix" | first run, shutdown/reopen, uninstall preserving user data | **NOT RUN** | ☐ PASS ☐ FAIL |

**Aggregate honest count:** requirements traced to at least one test vehicle: all areas above. Executed-and-green tests at time of writing: **0**. Therefore the mandatory-test requirement "no failing mandatory test" is currently vacuous, but "no UNVERIFIED critical area" is **not satisfied** until the Windows runs complete.

## 4. Packages, hashes and offline smoke (16.05)

| Artifact | SHA-256 | Built from FREEZE_SHA? | Offline clean-machine smoke |
|---|---|---|---|
| `AlTamam-Portable-win-x64.zip` | TBD (from `make-metadata.py` output in release workflow) | must be asserted by comparing workflow head-SHA | ☐ |
| `AlTamam-Installer-win-x64.exe` | TBD | same | ☐ |

Procedure: download both artifacts twice (CI + second machine), recompute SHA-256 independently, require equality with `build-metadata.json` before any smoke test. Offline smoke on a clean Windows VM with network disabled: first run → login → attendance entry → export → shutdown → reopen → verify data intact; then uninstall → verify `%LOCALAPPDATA%\AlTamam` DB/backup preservation per installer policy → reinstall → data visible. Evidence: screen recordings/logs attached to acceptance repo. State: **NOT RUN**.

## 5. External review (16.06)

ChatGPT review package prepared for owner upload: PR diff URL, `FREEZE_SHA`, test `.trx` artifacts, workflow logs, `PHASE14_SECURITY_REPORT.md` vulnerability list. Triage rule: external findings get severity (Critical/High/Medium/Low); fixes go in **new PRs branched off `FREEZE_SHA`**, which **re-open Phase 15/16 affected checks and produce a new freeze SHA**. State: **NOT STARTED** (owner must supply the review).

## 6. Security findings summary (carried from Phase 14)

To be pasted verbatim from `docs/security/PHASE14_SECURITY_REPORT.md` once generated (that generation was an open Phase 14 item). Until then: **UNVERIFIED — do not treat Phase 14 mitigations as tested claims** (14.07 distinction applies here).

## 7. Known limitations (complete list, no omissions)

1. No .NET SDK or Windows environment existed in the authoring sandbox; **zero tests have ever executed**. Everything in §3 marked NOT RUN stays NOT RUN until CI evidence exists.
2. Phase 14/15/16 work is committed locally (commits 06a46a8..the final bookkeeping commits on this branch; read exact tip with `git rev-parse HEAD` after push) but cannot be pushed from this sandbox (no remote credentials); Freeze SHA on main therefore undefined → 16.01 not yet met.
3. Dependency advisory audit (`dotnet list package --vulnerable`) never executed; pinned versions only.
4. Installer binary never compiled (Inno Setup unavailable locally).
5. WebView2 runtime distribution decision (Evergreen vs offline bootstrapper) pending owner approval (15.05).
6. Code signing: none; therefore **no 'secure' claim may appear in any release text** (15.08).
7. Canonical owner decisions from Gate 0 (e.g. «الرقم (ع)» uniqueness policy, reset semantics) remain partially unapproved and block closing several traceability rows.

## 8. Go / No-Go recommendation (16.07)

**NO-GO** as of 2026-10-23. Blocking conditions per Phase 16 gate: (a) freeze SHA not established, (b) zero executed mandatory tests, (c) packages not produced/verified, (d) security report incomplete, (e) independent verification workspace not provisioned. Recommendation: execute §1 freeze procedure → run `windows-release.yml` at FREEZE_SHA → fill §3–§5 with real logs → 16.06 review → resubmit this report for owner signature.

## 9. Owner authorization (16.08)

Release/tag creation requires the owner's explicit written acceptance **after** this report shows no fabricated evidence, no failing mandatory test, and no unaccepted data-integrity/security critical issue. Signature block:

```
Independent verifier: ______________  Date: ______  Run URLs attached: Y/N
Owner final acceptance & release authorization: ______________  Date: ______
Approved tag name: ______________
```
