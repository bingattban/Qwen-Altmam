# SECTION 05.05 — canonical standalone route handover (2026-10-31)

**Owner scope:** Keep existing screen appearance and structural hierarchy. Connect missing standalone entry points to existing authenticated Windows WebView2 main-window workflows, with no duplicated C# writes, new business policy, database schema, or new main destination.

**Base:** `main` at `8ada0be20ab4048112d0e4d0633471e9215a0ed1`. The subsequent PR's exact tested SHA, Windows results and packages must be verified separately; this document does not itself certify a release.

## What changed

- Reuse existing HTML modules and their unchanged CSS, headings, paths, and return links. Empty operation-only modules hand off to their canonical `index.html#RouteKey` main-window entry after validating an existing relative same-origin link. No new look, form, or second API caller.
- Main `functional-screens.js` resolves an existing route catalog key only after native C# authenticated session resolution. It renders only the four approved main destinations, then opens the existing administration screen owner where applicable.
- `admin-settings.js` maps archival/confirmation/modal aliases to their *parent operational list*, requiring explicit person/record selection and the normal original preview/confirm steps. Opening a named route never commits an attendance, import, transfer, separation, restore, or reset.
- Existing independently functional read-only modules (Administration, Sites, Statuses, PersonnelManagement, PersonPickerAndSearch, DraftsAndTemporarySave), the three dedicated dashboard module redirects, and generic informational SharedScreens are left intact. Standalone HTML remains as a compatibility path, not a second editable app.
- C# bridge role guards, event-time audit attribution, session security, encrypted SQLite, imports, backups and error codes are unchanged. GA-only audit entry presentation and restricted user management remain gated as currently approved.

## Responsible layers and evidence

| Layer | Ownership / contract | Authorization and errors | Transaction / tests |
|---|---|---|---|
| Phase 04 HTML/JS route handover | Existing route catalog, `data-route-key`, browser-local `index.html#RouteKey`, `AltamamFunctional.openRoute` | Unknown/missing route does not silently execute an action; authenticated session and native bridge remain authoritative | No database write; `build/test-route-return-link-honesty.js` covers every manifest route and standalones |
| Existing Administration owner | `AltamamAdminSettings.openOwnedRoute`; existing `attendance`, `persons`, `transfers`, `accounts`, `log`, and other real screen controllers | Supervisor denied Users, closed-day exceptions; GA-only audit entry editing remains restricted; all C# command guards unchanged | No new transaction. Existing modal, RBAC, audit, UI/E2E suites plus `build/test-admin-settings-navigation.js` |
| Authenticated startup | `GetCurrentSession`, `AuthenticateLocalUser`; route processed only with valid session | Session missing or forbidden -> existing login or denial; no pretend standalone credentials | No mutation; full Windows UI regression required |

## Still not certified

Routing legacy deep links to an existing action does **not** imply that every older screenshot-era route has an independent form, nor does it constitute clean-machine physical UAT. Modal aliases intentionally require selection within their canonical real workflow. Actual offline Windows UI/E2E and x86/x64 Portable/Installer evidence must come from the same tested PR SHA. Native destructive restore remains disabled until separately approved acceptance.
