# Owner-approved SECTION 2 — Mandatory Test-First Repair Process

**Effective 2026-11-05.** Applies to EVERY authorized corrective work package mapped to an existing PHASE 00–15. Not a new formal phase, production change, or production release approval.

## Verified adoption inventory (read-only snapshot)

- Repository `bingattban/Qwen-Altmam`, baseline `main` full SHA: `b09bef195b4cfb5ed905fd06d0cc65e82ba87cf0`.
- Historical successful main verification on this exact SHA: Windows Core run [42064885529](https://github.com/bingattban/Qwen-Altmam/actions/runs/42064885529) and Windows Release run [42064885730](https://github.com/bingattban/Qwen-Altmam/actions/runs/42064885730).
- Exact baseline: 69 canonical routes; 104 manifest bridge actions (44 mutating); 103 explicit native handlers; destructive `RestoreDatabaseAtomically` intentionally disabled.
- `Protect-main-Core-Release` ruleset ID `26457705` active with seven required Windows checks; PR-only normal merge and up-to-date branch checks. Do not bypass or rename required check names without owner authorization and coordinated ruleset transition.
- Production release stays NO-GO; independent offline physical Windows acceptance is deferred to after completion of improvements. Successful CI only produces tested candidate artifacts.
- This **documentation-only** adoption does NOT fix any production bug. Baseline RED and after-fix GREEN application tests: **NOT APPLICABLE** for this documentation change. For *every future code repair*, the genuine failing-test-first gate below is mandatory, without this exception.

## A. Inventory and classification BEFORE editing

Record a fresh full `main` SHA and relevant existing branches/open PRs, current ruleset, exact current Core/Release IDs, live manifests/migrations, UI screens and action consumers, SQLCipher schema, tests, artifacts and documentation. Identify source file/function/line and trace real ownership:

`UI screen + JS state → bridge-client.js → WebView2 host/dispatcher → canonical C# Application service → SQLCipher transaction/query → response, invalidation and immutable audit`.

Classify each finding precisely:
1. **VERIFIED DEFECT:** observed failure against approved behavior (reproduction required).
2. **ACCEPTANCE COVERAGE GAP:** real behavior insufficiently tested (do not claim defect or PASS).
3. **INTENTIONALLY DISABLED:** explicitly owner-gated capability (e.g. physical restore); never enable casually.
4. **STALE/UNUSED CANDIDATE:** check runtime, tests, contract, external and dynamic callers first; absence of one textual call is insufficient.
5. **POLICY CONFLICT:** old text, new owner decision, implementation, and consequences differ; owner must decide.

Requirement precedence: latest explicit owner decision → approved owner decisions in repository → surviving `جديد.txt` plan → prior nonconflicting approved behavior → nonconflicting implementation. Do not resurrect superseded rules.

## B. RED: failing executable regression first

Before changing **production** code:
- Add the narrowest meaningful test on the unchanged baseline. Native bridge/persistence faults require real request envelope + origin, C# dispatcher authorization, actual service and temporary encrypted SQLCipher database with commit/readback; mocked JS or static assertion is not a substitute. Real Windows GUI defects require real WPF/WebView2 execution (route-open alone is inadequate).
- Test exact defect and relevant invalid-input, unauthorized role, expected-revision conflict, replay/idempotency, cancellation, transaction rollback, immutable audit and historical-data preservation paths.
- Execute the new test before the production fix and capture the real failing assertion, test-only commit SHA, command, runner, test count and CI run URL. A test that fails solely because a dependency is missing is **not** valid RED evidence.
- When local .NET/Windows runner is absent, mark `NOT EXECUTED LOCALLY`. Establish an executable Windows harness and demonstrate RED through actual Windows CI BEFORE the fix; failed/test-only candidate must NEVER merge.
- If actual baseline RED cannot be reproduced, stop calling it a confirmed defect; reclassify as acceptance gap, already-fixed behavior or owner conflict.

## C. Minimal owning-layer correction

Only after meaningful RED, modify the canonical owning code and essential DTO/mapper/JS caller. Do not fork business SQL, duplicate services, create shadow bridge actions, add unused dependencies or rewrite working UI.

For every mutation explicitly describe: action/DTO and server-side allowed roles; input validation and Arabic failure/error code; expected revision, operationId/replay receipt and preview token as applicable; SQLCipher transaction/rollback; audit event and actor full-name snapshot; cancellation; permanence of PersonId and effective-dated history. Preserve founder identity, distinct 08:00/20:00/16:00 settings, monthly opt-in export OFF by default, SQLCipher provider and same Windows AppData root.

## D. GREEN: demonstrate real behavior and complete suite

- Re-run the *identical* RED regression on the fixed code; record observed after-fix PASS with precise SHA, test count, run and log. Do not infer from a static analysis.
- Run applicable xUnit/.NET, independent SQLCipher suite, JavaScript, real Windows WebView2 UI/bridge tests, native role/auth/session/replay checks, manifest and migration guards, and performance/output parity when relevant.
- Run Release compilation locally where supported; otherwise state `NOT EXECUTED LOCALLY` and provide actual Windows CI build evidence.
- Never skip, fake, xfail, ignore, quarantine or weaken an essential test for a green badge. No mock-based substitute for real required E2E.
- Classify any unrun *independent manual offline Windows acceptance* as OWNER-DEFERRED (not as passed); do not label a CI artifact production-ready.

## E. Evidence and conflict register (for EVERY package)

Capture in the pull-request description and supporting evidence file:
- Existing PHASE 00–15 and work item; baseline full SHA; root-cause classification; owner requirement citations; source path/function/line; consumers/call chain.
- Exact failing-test-only SHA, initial test command, observed failure + run URL, then fix SHA, same test pass + run URL; `NOT EXECUTED` for anything not actually run.
- Exact canonical action/DTO, server-side role authorization, validation, expected revision, operationId, idempotency/replay, SQL transaction/rollback, audit event, failure code, and UI output parity.
- Test success/failure/invalid/unauthorized/stale/replay/cancellation/persistence evidence; relevant performance baseline/after results.
- Conflict register: **finding ID | old requirement | newest approved requirement | current code location | proposed change | existing-data consequences | security/performance consequences | owner decision (APPROVED / PENDING)**.
- On unresolved material conflict, historical-data loss, destructive migration or uncertain role, **STOP the affected change/PR**. Work on independent unaffected tasks only when demonstrably safe.

## F. Protected release verification and merge

1. Open a scoped PR from current main; record final PR-head SHA and require up-to-date branch, no conflict or blocking review.
2. Windows Core and Windows Release run concurrently on the same latest PR head. Inside Release, ALL tests succeed BEFORE packaging.
3. Confirm all required jobs conclusively SUCCESS and the four customer artifacts exist: **x86 Portable ZIP, x86 Installer EXE, x64 Portable ZIP, x64 Installer EXE**, including build metadata and independent real-file SHA-256 validation. No failed, pending, cancelled or missing required checks.
4. Normal protected merge only with exact head SHA checked; **verify both new main SHA Core and Release workflow runs after merge**. Do not assume post-merge success.
5. Manual independent Windows UAT and native destructive restore remain distinct later approval gates. No final production publishing/GO before explicit owner decision.

**This is a process contract, not proof of untested application behavior.**
