# Section 03.06–03.15 — Implementation and acceptance evidence

> This is an engineering status matrix, not a final clean-machine acceptance certificate.
> Source of truth for founder policy: `docs/requirements/OWNER_DECISIONS_2026-10-24.md`, §7.

| Clause | Implemented control | Automated evidence / remaining manual validation |
| --- | --- | --- |
| 03.06 Admin | `RoleAuthorizationPolicy` allows ordinary admins to create/manage **Supervisor** accounts, correct closed days, and edit/hide visible audit overlays. Prevents Admin/GeneralAdmin account creation and whole-app reset. | `BridgeRbacMatrixTests`, `UserAccountManagementIntegrationTests`. |
| 03.07 Supervisor | Manifest-enforced denied user-management, audit overlay modification, closed-day correction, reset and administration-only archive operations. Standard attendance, roster, transfer, report and Excel permissions remain as declared. | Every declared action is tested for all three roles + unauthenticated in `BridgeRbacMatrixTests`. |
| 03.08 Enforceable C# permissions | `WebView2BridgeDispatcher` resolves the authenticated server role and checks every registered action BEFORE its handler executes. C# services separately validate sensitive account/reset/audit operations. | `BridgeRbacMatrixTests` covers forged role claims, unknown actions, hostile JSON, foreign origins, non-main frames, replay, and role/operation combinations. |
| 03.09 Password policy | Subsequent passwords: at least 8 characters, 3 ASCII letters, 3 ASCII digits and a special character. Argon2id (64 MiB, 3 iterations, 2 lanes, fresh 16-byte salt). Repeated login failures temporarily lock accounts. First-run founder retains stronger previously owner-approved 10-character minimum. | `UserAccountManagementIntegrationTests`, `IdleAndSharedOutputTests`, and authentication tests. |
| 03.10 Idle lock | Exactly 300 seconds since last genuine input. Session expiry is persisted and checked by SQLCipher. WebView2-originated `userActivity=true` alone must NEVER extend it. Native WPF input is authoritative; WebView2 user activity requires a successful authenticated `GetCurrentSession`, recent actual Windows input (<=2 seconds), **foreground process** verification, and refresh of the exact matched nonce only. | `IdleAndSharedOutputTests.ForgedWebViewUserActivityFlag_CannotRenewSessionWithoutTrustedNativeInput` + server expiry/restart tests. **Remaining**: manually test 299/300s, sleep/resume, input focus, locked-screen UX on clean Windows x64/x86. |
| 03.11 Draft isolation | Attendance drafts are in the encrypted database, scoped to UserId. Lock/logout never commits draft records. | `AttendanceDraftOwnershipIntegrationTests`. |
| 03.12 Logout only for switching | Server rejects login while another active session exists; explicit Logout revokes the prior nonce; JS clears pending requests/responses and session state. | `IdleAndSharedOutputTests`, bridge-client guards. |
| 03.13 Person exit | External transfer, separation and removal from active roster suspend affected *roster-linked* accounts and revoke sessions atomically. Person reactivation does not reactivate an old account; separate audited admin action required. | Movement, separation, lifecycle and user-account integration tests. |
| 03.14 User records | List includes sequential row index, A number, username, display full name, role, creator's frozen username-roster-name snapshot when creator has roster linkage. | `UserAccountManagementIntegrationTests`. |
| 03.15 Password-only edit | Account password edits explicitly reject attempts to alter username, role or linked PersonId. Disabling/reactivation use independent permission checks, account version and audit. | `UserAccountManagementIntegrationTests` and RBAC tests. |

## Non-negotiable founding administrator exception

- **Role 1 GeneralAdministration has no personnel record**: `LinkedPersonId IS NULL` forever, and no personnel (ع) number. Founder name `FoundingFullName` is OPTIONAL. The display is `الإدارة العامة - username`, with ` (الاسم الكامل)` added only if provided.
- V009 detaches any previously linked founder without changing the original UserId, username, password, activation state or historical audit evidence; SQLCipher triggers prevent direct re-linking. The former `LinkFoundingAdministrator` action has been removed from both manifests.
- This change deletes **obsolete** code paths that blocked external transfer, separation or archive of a person based on the fictional premise of a linked founder; normal roster-linked account suspension remains enabled, with an explicit `Role<>1` guard.
- Tested with `Section03FoundingAccountTests` and `PersonnelSeparationIntegrationTests.FounderStaysIndependentAndActive_WhenUnrelatedRosterPersonIsSeparated`.

## Mandatory release policy

Commit implementation only on a PR branch. Both Windows Core and Windows Release must conclusively pass on the exact PR HEAD SHA. Windows Release tests must pass **before** four x64/x86 Portable+Installer artifacts are built/uploaded. Do not merge a failed, pending, canceled, or mismatched build. Successful automated CI packages remain **release candidates**, not certified clean-machine acceptance on 32-bit physical Windows.
