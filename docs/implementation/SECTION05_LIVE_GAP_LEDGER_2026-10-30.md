# SECTION 05 — live implementation evidence and open gates (2026-10-30)

Scope: owner-approved **document section 05.01–05.05**. This is **not Phase 05 or Phase 06**, and creates no additional governance phase. Source baseline: `ee18d46f5b05b7fd67e883b0228afe8a93afe194` on `main`. No production policy, database migration, UI code, or RBAC rule is changed by this evidence-only update.

| Clause | Verified implementation | Remaining acceptance work | Owner |
|---|---|---|---|
| 05.01 | `screen-manifest.json` assigns screen ownership to existing phases; `windows-build.yml` and `windows-release.yml` exist | Do not treat old references to Phase 16 in historical documents as an approved new phase. Keep current work in phases 00–15 | Governance / Phase 00 |
| 05.02 | Server-side audit permissions and front-end audit route visibility were reconciled in PR #137 | Open, stale/conflicted PR #114 must be compared against current contracts; **do not merge or rewrite it without owner resolution if a new material policy conflict is found** | Contract / owner |
| 05.03 | `AlTamamDbContext.CurrentSchemaVersion=11`; existing bridge, UI and automated test code inspected | Re-read current `main` SHA, changed files, previous CI, DTO/action manifest, and migrations before every implementation batch; reuse real integrated main-window UI | Each owning phase |
| 05.04 | Existing `WebView2BridgeDispatcher`, `action-manifest.json`, C# application services and tests provide server authority | For every subsequent change explicitly record: owning layer, DTO/action, validation, role guard, error code, transaction, rollback and positive/negative tests in PR | Each change |
| 05.05 | `functional-screens.js`, `attendance-ui.js`, `admin-settings.js` implement real main-window workflows; `bridge-client.js` disables unsupported standalone refresh actions and labels them as development-only | At baseline, standalone HTML route files contained `يجري ربط الشاشة خلال مراحل تنفيذ المشروع`; the historical 2026-10-23 audit reported **69/69 placeholder-bearing routes**; by this PR\'s actual base, **65/69** still bore the placeholder. This PR connects **Sites** and **Statuses**, leaving **63/69** placeholder-bearing routes, as confirmed by the Windows Core preflight output on commit `2c45d6296a437f0848ca872a1f32289e36f0665b`. This count does **not** mean 63 broken main-window workflows. Replace or route each standalone surface to its authentic owner without duplicating business logic; verify Loading/Empty/Validation/Conflict/Forbidden/Success and every Save/Cancel/Confirm. | Owning UI phases, integration Phase 04 |

## Specific observed route behavior

- `screens/Dashboard/UnpreparedPersonnel/index.html` and `.../SelectStatusAndConfirm/index.html` load `assets/js/auto-attendance-route.js`, which redirects to `index.html#auto-prepared`. The actual main-window register supports individual/bulk changes, so **do not describe this workflow as read-only**.
- `screens/Administration/PersonnelManagement/Attendance/index.html` and `.../ConfirmAttendance/index.html` still contain the generic development shell, while `assets/js/attendance-ui.js` already performs preview, confirmed commit/revision and drafts. Reuse that owner rather than building a second transaction path.
- `assets/js/bridge-client.js` originally enabled real standalone refresh for `Administration`, `PersonnelManagement`, `PersonPickerAndSearch`, and `DraftsAndTemporarySave`. This PR also enables `Sites` and `Statuses` through the same existing authenticated read-only C# services. All other generic REFRESH controls remain explicitly disabled and labeled `قيد التطوير`; this is truthful, **not a functional acceptance pass**.

## Implementation batch — SECTION 05.03–05.05, no new phase

**Test-first:** Extended the existing `build/test-readonly-route-states.js` regression before editing production files. Both existing Windows workflow preflights already invoke this regression (the script is not a new, untracked test). The test exercises the *shipped* bridge client and real request envelopes, with a simulated C# response—not a substitute for an on-device WebView2 acceptance test.

| Change | Owning layer | DTO/action | Validation / permissions | Error handling | Transaction, rollback, test |
|---|---|---|---|---|---|
| Sites read-only module | Phase 04 WebView2 client + existing ConfigurationService | `QuerySites` → `{siteId,name,active}[]` | Existing C# authenticated session/role guard, allowed GA/OA/Supervisor; Supervisor can see only permitted sites; UI never grants permissions | Loading/Empty/Success, ValidationError, ConflictStale, Unauthorized, SessionExpired (canonical client mapping) | **Read-only**; no mutation, write transaction, rollback or audit change. Regression asserts correct action, visible states, no stale data and no write action |
| Statuses read-only module | Phase 04 WebView2 client + existing ConfigurationService | `QueryAttendanceStatuses` → active `{statusId,displayName,shortSymbol,color}[]` | Existing C# authenticated session/role guard, allowed GA/OA/Supervisor; no new policy or role capability | Same canonical client handling; malformed data rejected without displaying stale records | **Read-only**, no transaction/rollback side effects. Regression asserts correct action, real values, states, no writes |
| HTML links and state surfaces | Two existing Phase 05-owned HTML screen files only | `routeResults`, `routeSubmit`, `bridgeStatusBox` | No inline script, no external URL, same CSP; `index.html#admin` is an existing integrated section; no claim that the route opens an editor | Accurate initial prompt and populated result region; normal disabled/error/success handling in shared bridge | Back navigation opens real management UI; no parallel save path |

Files changed: `src/AlTamam.WebUI/assets/js/bridge-client.js`, `src/AlTamam.WebUI/screens/Administration/StatusesAndSites/{Sites,Statuses}/index.html`, `build/test-readonly-route-states.js`, and this ledger. Database schema V001–V011, 101-action contract, role guards, archive, idempotency and audit paths are untouched.

**Acceptance:** Both Windows workflows must finish on the **final PR head SHA** with zero failures; Release full tests must pass before real x86/x64 Portable/Installer packaging. Manual offline Windows/WebView2 acceptance remains OPEN. No unsupported screen is portrayed as finished.

## CI and release evidence

- At baseline SHA `ee18d46f5b05b7fd67e883b0228afe8a93afe194`, Windows Core run `41072324622` completed successfully. Windows Release run `41072324693` compiled and ran **922 passed / 0 failed / 0 skipped** C# tests; its x86/x64 packaging jobs were **still running at inspection**, so this document does not claim that baseline release succeeded.
- A green CI run proves the tested code and packaging checks, **not** manual offline clean-Windows acceptance, keyboard/RTL usability, or full screen-level behavioral coverage.
- Four required distributables remain: win-x86 Portable + Installer, win-x64 Portable + Installer, with SHA-256 and both workflows green on the **same** PR head and then merged `main` SHA. Do not merge on incomplete checks.

## Execution order and acceptance criteria

1. Audit each standalone screen against the authentic main-window owner and server action contract; classify as functional, intentional redirect, or explicitly incomplete. Never infer missing backend functionality from a placeholder HTML file alone.
2. Write failing behavioral tests **before** wiring a missing UI path. Assert the action is real or disabled truthfully, and cover all six UI states plus role/expired-session cases.
3. Implement the smallest compatible change in its owning layer, without new policy, duplicated bridge calls, migrations or unrelated UI changes. Preserve historical identities and immutable audit.
4. Open a feature PR. Run Windows Core and Windows Release concurrently, with full Release tests **before** both architecture packaging jobs. Verify four real packages and checksums on exact SHA before merge.
5. Complete physical Windows/WebView2 and offline restore/install acceptance separately. Report unfinished work as OPEN rather than DONE.

This file is a **current gap ledger**, not a claim that SECTION 05 is fully implemented or production-approved.
