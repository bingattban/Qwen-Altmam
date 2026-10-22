# AlTamam — AS-IS Architecture (Phase 00 / Step 00.04)

Verified against the CONNECTED REPOSITORY working tree at commit `1bb28bb` (base `main` @ `eb34116`), not only the supplied project.zip. ZIP correspondence: repo contains the same 103 baseline files plus 2 docs added in Phase 00 (105 tracked now); no structural divergence found.

## 1. Solution topology (AlTamam.slnx — 4 projects, NO executable host)

| Project | Target | Real content | Placeholder/absent |
|---|---|---|---|
| AlTamam.Domain | net10.0 | 16 entity records (Person, Site, AttendanceStatusDefinition, PersonAssignment, PersonnelMovement, AttendanceRecord, AttendanceRevision, AutoAttendanceRun, AuditEvent, AuditLogPresentationEdit, UserAccount, BackupRecord, CustomFieldDefinition + 3 name-history records); StrongIds value objects with creation timestamps; enums (UserRole, MovementType, AttendanceSource, OrganizationalStatus, MonthlyCellKind, CustomFieldType); RoleAuthorizationPolicy; ArabicTextNormalizer; PasswordPolicyValidator; ISystemClock | No repository interfaces/abstractions folder beyond ISystemClock; no aggregate behavior methods |
| AlTamam.Contracts | net10.0 | BridgeRequest/BridgeResponse envelopes; BridgeErrorCodes (24 codes); LocalWebUiOrigin (`https://manger.adwas.example`); UiInvalidationEvents (13 event contracts); ContractDtos | No dispatcher/handler interfaces implemented anywhere |
| AlTamam.Application | net10.0 | AssignmentPeriodPolicy.cs, AttendanceDayPolicy.cs — preliminary rule logic only | ZERO use-case/service classes for any of the 90 manifest actions |
| AlTamam.Infrastructure | net10.0 | AlTamamDbContext (SQLCipher connect, 32-byte key enforcement, WAL, foreign_keys, busy_timeout, V001 schema init, serialized write helper, integrity check); PasswordSecurity (Argon2id p=2 t=3 m=64MiB, 16B salt, 32B tag, FixedTimeEquals); V001_InitialSchema.sql (26 CREATE TABLEs, seeds «غير محدد» SITE-UNSPECIFIED + 4 statuses, uniqueness constraints, assignment-interval & attendance triggers, AutoAttendanceRuns ledger ~line 318) | NO repository implementations, NO migration runner beyond initial script, NO DPAPI key storage (0 refs), NO backup/restore/export code |
| AlTamam.WebUI (not a csproj) | static | 70 HTML files total = root shell `index.html` + 69 screen pages under `screens/<Area>/<…>/index.html`, matching screen-manifest.json 69/69 with 0 path mismatches; action-manifest.json (90 actions/11 services); assets/css; altamam-app.js (navigation + placeholder dashboards); screen-module.js (explicit placeholder that REFUSES business actions) | No real bridge binding: 69+ `data-bridge-action` markers unbound; strict CSP meta on all pages; 0 inline JS/handlers, 0 remote refs, 0 eval/fetch/localStorage (re-scanned this turn) |

## 2. Counts re-verified from repository (script output this turn)

- Screens: declared 69 = manifest entries 69 = on-disk screen pages 69 (+1 shell) ✔ (ASCII PascalCase routeKeys unique ✔)
- Actions: 90 across 11 owningService values (Attendance 7, Auth 6, Configuration 12, Dashboard 4, ExcelSheets 11, MonthlyGrid 3, Movement 11, PersonRoster 7, Reporting 6, SettingsBackup 13, UserAudit 10) ✔
- Mutating actions: 34; all 34 declare idempotentByRequestId:true ✔
- allowedRoles references: GA 90, OA 88, SV 74, Unauthenticated 2 ✔ (consistent with §2.9 tiering)
- Canonical error codes: 24; UI invalidation events: 13 ✔
- C# source files: 16 (.cs); SQL migrations: 1 (V001); test projects: 0; CI workflows: 1 (windows-build.yml: windows-latest restore+build Release only — no test/publish/installer job)

## 3. Implemented vs placeholder functional map

REAL today: encrypted-DB bootstrap & integrity primitives; password hashing/verification; role-authorization function; Arabic normalization; date-interval/attendance DB constraints; RTL page inventory & navigation JS.
PLACEHOLDER today: every business action (all 90), all dashboards/counters, import/export/backup/restore, scheduler (20:00 run table exists but nothing executes it), audit overlay workflow, session/auth flow wiring, WebView2 host & bridge dispatcher (do not exist).

## 4. Dependency direction compliance (§2.7)

Domain has no outward deps; Contracts standalone; Application→Domain; Infrastructure→Application/Contracts. Compliant as built; the future Host must be the only outermost layer. No business state lives in JS (placeholder dashboards explicitly non-authoritative).
