# Phase 03 — focused first-run/auth/session reconciliation

Scope 03.01–03.13; keep approved DesktopHost and static WebUI structure.
Retain SQLitePCLRaw.bundle_e_sqlcipher unchanged, with the SAME database key,
native provider, AppData, WAL, encrypted backup and recovery formats.

Existing account/session behavior is already implemented by BridgeSharedServices,
V006/V009/V011, PasswordSecurity (salted Argon2id), WindowsDatabaseKeyStore
(DPAPI), UserAccountManagementService, RoleAuthorizationPolicy,
WebView2BridgeDispatcher, DesktopBridgeGateway and session-idle.js.
Existing encrypted regression tests cover unique founder, unlinked founder,
roster eligibility, failed-login lockouts, 299/300 seconds, draft ownership
and in-flight logout cancellation.

**New safety coverage:** first-run SQLCipher authority checks BOTH Users and
the immutable founding audit event, including under the serialized bootstrap
transaction. If a legacy user row was accidentally deleted without clearing the
event, no second founder may be created. Only the explicit full-data reset
removes both. Foreground WPF events are gated on an active desktop window;
WebView2 input uses separate Win32 verification.

An independent Phase 03 unit project now validates all sensitive Users, audit
presentation and closed-day action role matrices, prohibition on another
GeneralAdmin, and password-strength policy. Both Windows CI workflows
must run it with real TRX nonzero counts before release packaging.

**Acceptance boundary:** Simulated-time tests and Windows WebView2 E2E do
not replace physical sleep/resume, clock-change, and clean-machine testing.
No downstream screens are declared complete by this backend phase.
