# Section 05.05 — Correct top-level route ownership (2026-10-31)

## Observed, reproducible functional gap
After PR #160 and #161, authenticated standalone `#RouteKey` navigation uses the real `AltamamNavigation.sectionForRoute` from `altamam-app.js`. This resolver previously only recognized nested paths beginning `screens/Dashboard/`, `screens/MonthlyAttendance/`, or `screens/Administration/`. The **root** files `screens/Dashboard/index.html`, `screens/MonthlyAttendance/index.html` and `screens/Administration/index.html` do not contain those prefixes because the slash is after the folder rather than before `index.html`. Each therefore fell through to `settings`, despite being a real root for a different primary destination.

The same helper is used by the canonical route tree when its fallback path is displayed. This is not a new UI design or business feature: it is a routing correctness fix in the existing owner layer.

## Contract and behavior
- **Owning layer:** Phase 04 navigation and Section 05.05 functional handover only.
- **DTO/bridge action:** no additions, removals or mutations; 69 canonical paths, 103 bridge actions unchanged.
- **Validation:** the exact `screens/<folder>/index.html` root or an immediate `screens/<folder>/` descendant maps to its one approved top-level destination. Prefix collisions never match. All other routes retain the existing Settings fallback.
- **Permissions and errors:** existing C# bridge session and RBAC remain authoritative; no changes to user roles, unknown-route handling or error codes. Authentication is still required before dispatch.
- **Transactions/rollback:** zero database writes and no migration; reverting the JS mapping restores the previous routing, but should not be necessary after regression validation.
- **Tests-first:** extend `build/test-dashboard-route-navigation.js` to execute the actual shipped `altamam-app.js` resolver and `functional-screens.js` authenticated dispatcher. Assert the four canonical root destinations, seven representative nested/shared routes, four real dispatched routes and fail-closed unknown names. The existing Windows Core and Release preflights invoke this script, and full Windows C# and packaged UI checks must pass.
- **Scope boundary:** leave all HTML/CSS, the owner's visual layout, native destructive restore (disabled), archival identity, scheduled policy and other open work unchanged. No separate test artifact should be described as clean-machine physical UAT.

A prior historical acceptance ledger's 170×35 search-control dimensions and physical Windows keyboard/RTL, unplugged-network, cross-PC restore, interruption, and actual UAT gates remain separate. Do not claim they are solved by this narrowly scoped route fix.

## Additional existing-authorization UI gap — audit history
The canonical `QueryAuditPresentationChanges` action is expressly readable by `GeneralAdministration` **and** `OrdinaryAdministration` in the current C# action manifest. The main Administration UI previously rendered the `القيود المعدلة والمحذوفة` history button only for GeneralAdministration, despite the pre-existing `canSeeRoute` permitting ordinary administration's history route. Also `ModifiedDeletedAuditEntries` was mapped to generic `log` rather than opening the real history panel.

- **Owning layer:** Phase 11 audit read UI under Section 05.05 route integration.
- **DTO/actions:** no changes; reuse the existing read-only `QueryAuditPresentationChanges` action and its page-size 50, existing text and canonical error handling. The edit/hide `UpdateAuditPresentation` and `DeleteAuditPresentation` actions remain restricted to GeneralAdministration in both UI and C#.
- **Validation/role:** only GA/OA may reveal history via the button or direct route; supervisor is explicitly denied by the existing route policy and administration entry point; missing session rejected before route navigation.
- **Transactions:** the route only opens an existing read-only modal and loads the history once, never commits, mutates, refreshes an audit overlay, or bypasses the selection/confirmation process.
- **Tests:** extend the existing Windows-enrolled `build/test-admin-settings-navigation.js` test for ordinary-admin read-only access, deep-link opening and actual bridge read, supervisor denial, GA access, and absence of any `UpdateAuditPresentation` or `DeleteAuditPresentation` write from navigation. The rest of the audit-detail keyboard and cancel tests remain in place.

Clean-machine Windows and manual author signoff remain separate outstanding gates, not implied by CI.
