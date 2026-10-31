# SECTION 05.05 — functional Settings deep-link wiring (2026-10-31)

Scope: implement the *next missing part* of existing standalone-route handover. The owner requires **no screen redesign**: do not edit HTML/CSS, icon, WPF layout, domain policy, SQLCipher schema or audit history.

## Current verified gap
The previously merged route dispatcher resolves all named `Settings/*` routes to the four-destination Settings root, but discards their individual route keys. Users following `CreateBackup`, `RestoreBackup`, `ScheduledBackups`, `AppearanceAndDiagnostics`, `AboutApplication`, `Logout`, `ResetApplicationData`, and the shared `EightPmScheduler` link are left at the generic Settings page without focus on the already implemented action.

## Minimal change and contracts
- **Phase 04 UI integration and Phase 12/13 Settings owner** only. `AltamamFunctional.render("settings", routeKey)` forwards the authenticated, catalog-resolved local route to existing `AltamamAdminSettings.renderSettings(session, logout, routeKey)`.
- After verified `GetSettingsOverview` and `GetApplicationVersion` have returned and the current render token is still live, the Settings owner **focuses/scrolls to the existing action**. No click, server mutation, request reauthentication bypass, or modal confirmation is performed by navigation alone.
- Routes: `CreateBackup` -> manual backup button, **or the existing Windows folder-picker button** when no writable directory exists; `ScheduledBackups` -> save schedule; `RestoreBackup` -> read-only file inspector; `AppearanceAndDiagnostics` -> database integrity check; `EightPmScheduler` -> administrator attendance-policy save; `ResetApplicationData` -> GeneralAdmin-only reset **preview**; `Logout` -> existing logout button (**never auto-logout**); `AboutApplication` -> read-only account/version panel (no dangerous button).
- Existing C# per-action authorization and the session remain authoritative. In the UI route guard, a Supervisor cannot navigate directly to `ScheduledBackups` or `EightPmScheduler`, matching the existing administration-only editable controls. GA alone can see `ResetApplicationData`. A Supervisor can still inspect an encrypted backup but cannot preview/commit a restore.
- `RestoreDatabaseAtomically` remains disabled. Unavailable/corrupt paths keep the existing errors and the output-folder chooser; no success is fabricated. User selections, stale response checks, preview tokens, idempotency and confirmations stay in their existing service owners.

## Tests and acceptance
- Extended **existing** `build/test-admin-settings-navigation.js` to cover all eight named deep links and verify each focuses the correct actual control without automatic backup, restore, schedule, integrity, reset or logout operations. Supervisor lacks dangerous controls.
- Extended `build/test-audit-route-visibility.js` to 32 route-role checks and tested route forwarding and denial of malicious deep links. Both scripts are already invoked by both Windows CI workflows.
- No data writes or migrations from opening a route; rollback is a regular code revert. Run real Windows Core/Release test gates on identical HEAD SHA, then build/verify x86/x64 Portable/Installer and SHA-256 before merging. Real clean-machine offline UI UAT and native destructive restore approval remain **OPEN** until independently evidenced.

This is one bounded functional-integration increment, not a certification of all 69 screens or of the entire owner directive.
