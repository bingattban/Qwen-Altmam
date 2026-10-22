# AlTamam Implementation Ledger

Governing process: the Qwen Coder master implementation directive (owner-added). Canonical
requirements source: `docs/implementation/AlTamam_QwenCoder_Master_Implementation_Prompt_EN.txt`
— **NOT YET PRESENT IN REPOSITORY** (requirement-source gap recorded in Phase 00 evidence).

Rules of this ledger (contract §2.3–§2.5):
- Work order is PHASE -> STEP -> SUBTASK. Exactly ONE authorized STEP at a time; the owner decides
  when Qwen proceeds to the next step or phase.
- File batches: max 10 complete changed/new source files per step; coherent changes needing more are
  split into explicit substeps. Every affected file tracked with exact relative path + status
  (NEW / MODIFIED / MOVED / DELETED) + necessity explanation.
- Nothing labeled "complete" if it contains stubs, TODO-only service implementations, fictitious event
  handlers, test bypasses, fake counters, placeholder data, or silent no-op actions.
- All code committed in full to the working branch; no uncommitted partial patches; chat excerpts are
  never source truth — the repository is.
- Missing prerequisite dependency => step marked BLOCKED, never "passed".
- Test-first gate (§2.4): read requirements -> inspect code -> targeted failing regression tests where
  feasible -> implement -> restore/build -> unit/integration/security tests -> diff+migration review ->
  CI evidence -> owner approval. Failed test = failed gate. Windows builds MUST run on a Windows
  GitHub Actions runner or real Windows machine; browser UI is not proof of Windows desktop success.
- No CI/build claims without a real GitHub Actions run URL + commit SHA + checks/artifact list.
- RETURN AFTER EVERY STEP using the §2.5 completion-evidence template (see PHASE00_EVIDENCE.md
  "Completion evidence (contract §2.5)" section).
- One row per phase/step. A step is DONE only when its evidence exists and the owner approved it.
- Quality/security rules (contract §2.10), verified against source where applicable:
  * Parameterized SQL only; no untrusted string interpolation into SQL text. Static scan of all .cs
    found zero interpolated/dynamic SQL today (DbContext executes bundled migration files verbatim);
    every future query must bind parameters.
  * Tests use synthetic fixtures ONLY; production personal data may never enter a test repo/artifact.
  * No default shared privileged credential, hardcoded key, recovery password, dev backdoor, or
    authorization bypass; DB key comes from caller-supplied 32-byte hex (AlTamamDbContext validates
    length/format) — plaintext production DB prohibited (SQLCipher enforced via PRAGMA cipher_version).
  * Passwords: existing Argon2id subsystem (PasswordSecurity.cs: salt 16B, tag 32B, parallelism 2,
    iterations 3, memory 64 MiB, FixedTimeEquals verify). Parameters to be reviewed against current
    OWASP guidance during Phase 01+ with an explicit before/after decision recorded (changing them is
    a semantics change => owner approval required).
  * Per-install DB-key protection: NO implementation exists yet (grep: zero DPAPI/ProtectedData
    references). Planned as a Windows-only Infrastructure component (DPAPI CurrentUser or approved
    store) with backup/recovery + reinstall/restore tests containing no embedded secrets. Windows
    behavior provable only on Windows CI/real machine.
  * WebView2 host: NO host exists yet. When built, navigation MUST be default-deny: exact allowed
    origin https://manger.adwas.example only (virtual-host mapping per LocalWebUiOrigin.cs), no remote
    navigation, reject privileged messages from untrusted frame/origin, no external file access, no
    unapproved web requests.
  * CSP strict, no unsafe-inline/unsafe-eval, local assets only — re-verified clean across WebUI
    (0 inline handlers, 0 eval, 0 fetch/XHR/WebSocket, 0 localStorage/sessionStorage in JS).
  * RTL Arabic vocabulary, accessibility, keyboard navigation, no interface jitter, existing visual
    proportions preserved unless owner approves changes.
  * Offline dependency packaging; installer + portable must run offline on supported Windows after
    prerequisites; dependency/advisory/license/package-integrity checks are part of CI hardening
    (dotnet nuget audit / license scan) once the test job lands.

| Phase | Scope (per screen-manifest phase tags until canonical doc arrives) | Status | Evidence | Owner approval |
|-------|--------------------------------------------------------------------|--------|----------|----------------|
| 00 | Baseline audit of connected repo; verify structure, manifests, CSP, origin; record gaps | COMPLETE — submitted | docs/implementation/PHASE00_EVIDENCE.md | PENDING |
| 01 | (Proposed) test backbone + CI hardening + host/bridge skeleton — subject to canonical doc | NOT STARTED (blocked on Gate 0 + approval) | — | — |
| 02+ | Per canonical master requirements archive once placed in repo | NOT STARTED | — | — |

## File-batch tracker (contract §2.3) — exact relative paths, status, necessity
All batches so far are documentation-only; zero production files touched; every batch <= 10 files;
working branch contains no uncommitted partial patches (`git status` clean at each commit).

| Batch (commit) | File path | Status | Necessity |
|---|---|---|---|
| Phase 00 (`97a2946`) | docs/implementation/PHASE00_EVIDENCE.md | NEW | Audit evidence required by directive ("maintain acceptance evidence as separate project docs") |
| Phase 00 (`97a2946`) | docs/implementation/IMPLEMENTATION_LEDGER.md | NEW | Implementation ledger required by directive |
| §2.2 recording (`bb4631c`) | docs/implementation/IMPLEMENTATION_LEDGER.md | MODIFIED | Record repo baseline identification, capability gaps, secret/data hygiene per contract §2.2 |
| §2.3–§2.5 recording (this commit) | docs/implementation/IMPLEMENTATION_LEDGER.md | MODIFIED | Encode stage/step/file-batch discipline, test-first gates, and completion-evidence template into the governing ledger |
| §2.3–§2.5 recording (`8e5e867`) | docs/implementation/PHASE00_EVIDENCE.md | MODIFIED | Add §2.5-template completion report for Phase 00 so every step's return follows the mandated format |
| §2.6–§2.9 recording (governance turn) | docs/implementation/IMPLEMENTATION_LEDGER.md | MODIFIED | Encode handoff rules, architecture boundaries, invariant map, role matrix (commit SHA to be filled once that turn's commit is confirmed on this branch) |
| §2.10 recording (this commit) | docs/implementation/IMPLEMENTATION_LEDGER.md | MODIFIED | Codify quality/security rules with source-verified status of each |
| §2.10 recording (this commit) | docs/implementation/PHASE00_EVIDENCE.md | MODIFIED | Add §10 static security-scan evidence (SQL interpolation, Argon2 params, DPAPI gap, CSP re-scan) |

## Repository baseline identification (contract §2.2) — verified 2026-10-23
- Working branch: `qwen-code-3daf4f97-7cb4-4a54-af89-c5f4e69a0b40` @ `97a2946b165ae3330ca9fe640af7c7701774bec0`
  ("Phase 00: baseline audit evidence + implementation ledger", docs-only, 2 files, +127 lines).
- Base branch present locally: `main` @ `eb34116` (shallow/grafted clone — full history not available in sandbox).
- Working-tree state at time of check: CLEAN (`git status`: nothing to commit).
- Actual selected GitHub repository URL: NOT DETERMINABLE FROM SANDBOX — `git remote -v` is empty and no
  credential/gh CLI exists here. The repository identity is established only by the Qwen Coder web
  connection on the owner's side. Per §2.2, we do NOT assume an account/repo from any previous session.
  This section must be updated with the real URL + branch + HEAD SHA once the owner confirms/pushes.
- Push / PR creation capability: ABSENT in this environment. Nothing has been pushed; no PR exists;
  no force-push, branch deletion, or history rewrite was performed or will be without owner authorization.

## Open items / blockers
1. **Gate 0:** canonical master requirements + step-acceptance archive missing from repo. Owner to place file(s) or approve substitutes.
2. Environment lacks git remote & gh CLI from sandbox → push/PR/Actions verification must be done via the Qwen Coder web connection by the owner; no such actions claimed.
3. No dotnet SDK locally → build/test verification deferred to Windows GitHub Actions runner after Phase 01 approval.
4. §2.2 gap: actual repository URL unconfirmed from inside the sandbox (see baseline section above). Awaiting owner-confirmed repo URL + approved base branch before any PR workflow is proposed.

## Secret/data hygiene check (contract §2.2, performed 2026-10-23)
- Scanned tracked files for embedded credentials/keys: no matches for secret patterns (password=, api_key,
  BEGIN PRIVATE KEY, connection strings with passwords) in source or docs.
- V001_InitialSchema.sql seeds NO privileged user/password rows (verified in Phase 00 evidence).
- No .db/.sqlite/.xlsx personnel data, no recovery codes, no binaries committed (repo = 103 text/config/source files).
- New commits continue to contain documentation only until Phase 01 approval.

## Decision log
- 2026-10-23 — Phase 00 executed as static audit only; documentation-only changes
  (`docs/implementation/PHASE00_EVIDENCE.md`, this ledger). Zero production-code files touched.
- 2026-10-23 — §2.2 baseline recorded from live git state (branch, SHAs, clean tree, absent remote).
  Feature-branch/PR naming proposal submitted to owner: `feat/phase-NN-step-MM` → PR onto owner-approved
  base branch (candidate: `main`). No merges without explicit owner authorization. Full-file reads required
  before any edit (this ledger edit followed a full `cat` of its current content).
- 2026-10-23 — Contract §2.3–§2.5 codified into ledger rules + file-batch tracker; Phase 00 re-reported
  in the §2.5 completion-evidence template inside PHASE00_EVIDENCE.md. Test-first gate interpretation for
  this repo: targeted regression tests are NOT FEASIBLE yet because zero test projects exist and no
  dotnet SDK is present in the sandbox; per §2.3 the first test-writing step therefore becomes part of
  Phase 01 scope (test backbone), and every later step must ship failing-test-first evidence. Recorded as
  dependency-blocked rather than bypassed.

## Ledger integrity correction (honesty note, 2026-10-23)
The previous governance-turn reply cited commit `a5e34d6` and claimed §2.6–§2.9 content was committed.
Verification of live git state shows that commit does NOT exist on this branch (log: eb34116 -> 97a2946 ->
bb4631c -> 8e5e867). The §2.6–§2.9 narrative therefore exists only in chat, not in repository files — per
§2.3 "chat excerpts are never source truth", it is being RE-DONE as a real commit now, together with §2.10.
No GitHub action was performed or claimed; this correction itself is part of the current commit.

## Architecture boundaries (contract §2.7) — codified 2026-10-23
Layer order: WPF Host -> authenticated WebView2 JS/C# bridge -> Application use cases -> Domain policies
-> repository abstractions/Infrastructure -> SQLCipher DB. Dependencies point INWARD only:
- No Domain reference to WPF/WebView2/Infrastructure (verified: current csproj graph already complies —
  Domain references nothing; Contracts references Domain only; Application references Domain+Contracts;
  Infrastructure references all three).
- JavaScript/localStorage hold UI state ONLY; business-state source of truth is C#+SQLCipher. Current WebUI
  JS contains zero storage/network APIs (scanned), so the boundary holds on baseline.
- The bridge dispatcher (future step) MUST treat renderer-supplied role, user ID and site authorization as
  UNTRUSTED claims: identity/session context resolved in C#; every mutation re-authorized by Domain policy
  server-side-equivalent before transaction commit. Local cache is never an authorization mechanism.
- All service methods: async I/O with CancellationToken, structured results, explicit error types from the
  existing Bridge error-code set (24 canonical codes) — no exception-as-control-flow across the bridge.

## Exact invariant requirements (contract §2.8) — enforcement map + test obligations
| Invariant | Current enforcement source | Gap / planned proof |
|---|---|---|
| Immutable canonical IDs (Person/Site/Status/Assignment/Movement…) | StrongIds.cs (durable IDs + creation timestamps) | Regression tests that update attempts fail; snapshots retain effective dates |
| «الرقم (ع)» textual, not numeric PK; normalize for search/uniqueness per canonical policy; preserve display text | Schema stores it as TEXT | NORMALIZATION RULE UNSPECIFIED => requirement-source gap (Gate 0); do not invent |
| Date intervals [start, end) inclusive/exclusive | V001 assignment-interval triggers | Trigger unit tests incl. boundary day == end date |
| No overlapping active assignments per person | V001 overlap trigger | Same-day edge-case tests |
| Exactly 0..1 committed attendance status/person/local-day | V001 uniqueness + AttendanceDayPolicy.cs | Concurrency test via serialized write helper |
| Transfer-effective date prohibits normal attendance; movement+assignment transactionally consistent | Policy partial; Movement actions in manifest | NOT YET ENFORCED end-to-end => future step with failing-test-first |
| Closed dates not silently writable; override = authorized+audited workflow | Statuses seeded; audit tables exist | Workflow unimplemented; blocked on canonical rules |
| Historical reports resolve names/sites/statuses AS-OF date; never rewrite snapshots on rename | History/snapshot tables in V001 | Dedicated historical-reporter component; rename-does-not-touch-history test |
| Audit evidence immutable; edits/deletes are overlay/tombstones, never physical deletion (except approved full reset) | Audit tables present | Enforce in service layer; tombstone tests |
| Day/month/archive/dashboard counters & Excel exports from same committed SQL facts | None yet (dashboards are placeholders) | Single query-layer rule + parity tests |
| Mutations: atomic transactions + request-level idempotency per action-manifest.json | Manifest declares idempotency keys | Dispatcher must dedupe; replay tests |
| Data stays on local Windows device except explicit export/backup; no telemetry | Zero network calls in JS; no HTTP client packages | Advisory scan in CI; egress tests |

## Roles and dangerous operations (contract §2.9) — snapshot verified against source
RoleAuthorizationPolicy.cs (read in full this turn) confirms: tiers GeneralAdministration / OrdinaryAdministration
/ Supervisor; GA creates OA & SV; OA creates SV; SV manages no users; full-DB reset and emergency closed-day
override restricted to GeneralAdministration. GA/OA may edit/delete VISIBLE audit entries while underlying
evidence stays immutable (overlay model matches §2.8 row above). CAUTION recorded: exact dangerous-operation
semantics must be re-confirmed against newer owner requirements (Gate 0) before implementation — §2.9 itself
requires this confirmation; we do not extend or relax the matrix without approval.
Obligation: exhaustive allow/deny test matrix across ALL 90 manifest actions × 3 roles (270 authorization cases,
plus sensitive-action double-confirmation cases) starting Phase 01+; sensitive delete/reset/restore/account/
historical-correction flows require explicit confirmation dialogs + structured audit rows.
