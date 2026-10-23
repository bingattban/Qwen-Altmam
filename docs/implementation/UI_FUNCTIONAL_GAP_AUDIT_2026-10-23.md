# التمام اليومي — تدقيق تكامل واجهات Windows

**Date:** 2026-10-23  
**Repository:** `bingattban2/Qwen-Altmam`  
**Audit scope:** application root HTML/JavaScript/CSS, all 69 route HTML documents, both route and action manifests, WebView2 desktop host, installation script, and available Windows CI evidence.

## Final finding — NO-GO for functional release

The application **compiles and passes 506 existing C# tests**, and real WebView2 can load the bundled HTML. That does **not** establish a working end-user application.

**All 69 of 69 route HTML files were read directly from the repository. All 69 contain the placeholder statement `يجري ربط الشاشة خلال مراحل تنفيذ المشروع`. None of the 69 contains an authored HTML `<form>` or `<input>`.** All 69 contain the same general REFRESH button. This proves UI shell coverage, **not** screen-level data entry or operations. The authentication page, for example, has no username/password form.

The C# action manifest declares **90 typed actions**. The current route refresh bindings in `assets/js/bridge-client.js` cover only `Administration → QuerySites`, `PersonnelManagement / PersonPickerAndSearch → SearchPersons`, and `DraftsAndTemporarySave → ListAttendanceDrafts`, while two other declared route refresh mappings are empty and default to an application-version request. The remaining business operations are not demonstrated through actual screen controls.

### Verified screen inventory

| Section | HTML routes | Screen-level form inputs |
|---|---:|---:|
| الرئيسية / Dashboard | 3 | 0 |
| التمام الشهري / MonthlyAttendance | 2 | 0 |
| الإدارة / Administration | 48 | 0 |
| الإعدادات / Settings | 8 | 0 |
| Shared screens, authentication and supporting flows | 8 | 0 |
| **Total** | **69** | **0** |

**The `uiAvailability=INTEGRATED_HTML_CSS_JS` manifest label must not be interpreted as proof that the relevant workflows are implemented.**

## Verified defects and corrections on this branch

1. **Missing Windows caption controls on smaller displays:** The main WPF window was initialized with fixed `1280 × 860` coordinates and `CenterScreen`. A screen whose work area is shorter than 860 px can position the caption buttons outside the visible display. Initial dimensions now fit the work area while retaining the standard resizable Windows title bar, minimize, maximize, close, and taskbar integration.
2. **Duplicated navigation:** `altamam-app.js` and `bridge-client.js` both independently populated a screen index. The second layer showed raw English route keys and mounted nested iframes. Replaced with one RTL, hierarchical route directory and direct same-origin navigation; removed iframe-based handling that could confuse main-frame authorization checks.
3. **Top-level names:** Primary UI navigation now displays exactly `الرئيسية`, `التمام الشهري`, `الإدارة`, `الإعدادات`. Technical route keys and folder paths are kept unchanged for compatibility. Corresponding manifest Arabic root titles and module root headings are aligned.
4. **Broken screen return loop:** Module links ending in `#RouteKey` formerly reopened the same module. They now return to its owning primary section.
5. **Duplicate fake REFRESH status:** `screen-module.js` used to show a static “not connected” message on clicks even when the bridge client separately handled a request. Removed the duplicate handler, without inventing functional write operations.
6. **Installer display name:** Inno Setup `AppName`, uninstall name, Start Menu and desktop shortcut, and launch description now use `التمام الشهري`. The stable `AppId`, physical app installation directory, executable name and user-data paths stay unchanged to avoid disrupting upgrades and encrypted databases.

## Outstanding functional gaps — required before final release

| Priority | Area | Required implementation and evidence |
|---|---|---|
| P0 | First-run / login / session | Real RTL first-run and sign-in forms; connect to `GetProvisioningState`, `AuthenticateLocalUser`, `GetCurrentSession`, `Logout`; validate session expiration and RBAC |
| P0 | الرئيسية / daily attendance | Real daily roster, counters, site filters, missing-person list, attendance selection, previews, save/revise, and optimistic-conflict errors wired to the bridge |
| P0 | Integrity / authorization | Per-screen protected write actions; dangerous operations require validated preview/confirmation; no UI bypass of C# RBAC, stable IDs, SQLCipher keys or history policy |
| P1 | التمام الشهري | Period/site filters, read-only monthly grid, archived periods, cell details, and actual Excel actions with meaningful loading/empty/error states |
| P1 | الإدارة | Personnel CRUD, sites/statuses, internal/external movements, separation, import/export, accounts, audit journal, and exception handling |
| P1 | الإعدادات | Real backups, restore preview/verification, scheduled backups, theme, diagnostics, and guarded reset flows |
| P1 | UX polish | Visual consistency, Arabic captions/breadcrumbs for all nested modules (without renaming route keys), nested navigation/back paths, keyboard focus, responsive windows and DPI |
| P0 gate | Functional acceptance | A real Windows UI test for each critical action and independent scenarios: first-run → login → attendance → monthly → import/export → backup/restore; installer and Portable on clean Windows offline, upgrade and uninstall preserving data |

## Verification rules

- Keep the **506 existing tests**; passing them is necessary but insufficient.
- Require `dotnet restore`, Windows Release build, route/manifest guard and complete `dotnet test` to remain green after UI/packaging modifications.
- Test actual WebView2 navigation (already introduced in earlier CI), then exercise interactive screen controls against real `DesktopBridgeGateway` and a synthetic encrypted database.
- No release tag or “fully complete” claim until the user-facing flows above and the independent final acceptance report have objective evidence.
- Changes to visible labels must **never** change route keys, authenticated action names, database schema, IDs, audit history, or encrypted user data.

## Important scope distinction

The present PR repairs the **visible navigation, compact-screen window chrome, labels, return paths and installer name**, and supplies this evidence-based deficiency audit. It does **not** implement all 69 business screens. Those workflows must be implemented and tested in subsequent feature changes rather than hidden behind green compilation checks.
