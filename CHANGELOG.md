# Changelog — Al-Tamam Daily (التمام اليومي)

## Unreleased — Phase 15 automatic-register receipt and retry safety (2026-11-03)
- The read-only automatic-prepared register now accepts a revision as saved only with a strict integer native receipt matching every selected PersonId; malformed/mismatched receipts remain uncertain.
- Only ambiguous `BridgeTimeout`/`BridgeOffline` permits an explicit second human review and replay of the exact same previewToken and operationId. Definitive conflicts, changed selections and navigation never reuse the attempt.
- A successfully committed change stays confirmed even if reloading the register fails. Added test-first dashboard/automatic-register regressions; no SQLCipher, role, theme, scheduler or other business-policy changes.

## Unreleased — Phase 15 real Windows modal transfer acceptance (2026-11-03)
- Extend the existing isolated WPF/WebView2 + encrypted SQLCipher selfcheck to open the actual internal-transfer review modal, click Cancel and prove no movement, click final Confirm and verify the native receipt, then replay the identical idempotency intent safely.
- Verify **exactly one** archived transfer, destination assignment and immutable TRANSFER_INTERNAL audit record in the disposable SQLCipher database. Require the trace marker for both pre-package Windows test flows and packaged x86/x64 Portable/Installer bridge checks.
- This is a bounded synthetic CI acceptance subset, NOT a full independent clean/offline Windows or recovery signoff. No production business rule, UI style, schema or activation behavior changed.

## Unreleased — Phase 15 full monthly Excel historical identity parity (2026-11-03)
- Extend the synthetic SQLCipher 10,000-person fixture with **effective-dated persisted identity-history rows** for all personnel. The earlier fixture lacked these snapshots and its Excel export benchmark checked worksheet row count, not content.
- After the real production XLSX export, stream-parse **every one of the 10,000 monthly rows** and verify ordinal, historical service number, historical Arabic name, site, and all 31 committed daily status cells against independent fixture truth. Also reject formula-bearing cells.
- Preserve the existing measured export duration, file size and SHA-256 evidence, and require an explicit truthful `monthlyExcelHistoricalIdentityAndDailyValuesVerified` field in the pre-package CI verifier. No application runtime or business-rule changes.
- This remains synthetic Windows CI service-level evidence, **not** independent offline Windows GUI acceptance or a production release approval.

## Unreleased — Phase 15 bounded native monthly XLSX batching (2026-11-03)
- The 10k-person real monthly XLSX benchmark exposed expensive repeated SQLCipher COUNT and historical ordering across 100 separate 100-person read pages. Preserve the real exporter, date-specific attendance, movement and archived identity rules.
- Add a native C# export-only monthly read path capped at 500 records per batch (below conservative SQLite variable limits), while leaving WebView2 interactive monthly requests capped at 100. Export now uses 20 bounded reads rather than 100 for 10k people.
- Add test-first full historical person/snapshot parity between the existing 100-person UI projection and the larger server-only export projection. Re-run both protected Windows workflows; no GUI/theme, schema, restore, security or external API contract changes.
- Synthetic Windows performance measurements are runner-specific; do not claim production throughput or independent offline GUI acceptance.

## Unreleased — Phase 15 measured 10,000-person monthly XLSX export (2026-11-03)
- Extend the existing SQLCipher 10k-person / 550k-attendance-record Windows diagnostic to call the REAL production monthly XLSX exporter against the same synthetic data, not a mocked writer.
- Check the actual native OpenXML workbook, every expected 10,003 worksheet rows, ten-thousand-person server receipt, no .part remainder, measured export time, file size and SHA-256. Delete the disposable test file after the run.
- The strict performance-evidence verifier now requires these measured XLSX fields BEFORE any Windows packaging. Do not conflate a synthetic CI Excel service benchmark with a complete WPF GUI/user-workflow performance acceptance. No runtime product, SQLCipher schema, scheduling, UX or release naming changes.

## Unreleased — Phase 15 verified post-merge evidence refresh (2026-11-03)
- Captured fixed `main` SHA `a48538b479d46580acffcbe5fe524a5b0c70d29c`, successful same-SHA Core/Release runs, the four real candidate customer-file SHA-256 values, the measured 10k/550k-row SQLCipher workload and the Windows smoke evidence.
- Distinguished measured database diagnostics from **NOT EXECUTED** full GUI/Excel and clean/offline acceptance; recorded the enabled `main` ruleset and kept native restore and production owner signoff **NO-GO**.
- Updated the acceptance matrix and historical report pointers without changing runtime code or claiming that CI packaging constitutes independent production approval.

## Unreleased — Phase 15 attendance confirmation and safe retry (2026-11-03)
- Require a real native commit count matching the preview before marking daily attendance saved; mismatched/absent receipts are uncertain, never successful.
- Allow same-preview and same-operationId retry only for ambiguous bridge timeout/offline after a new explicit review. Definitive native failures require a fresh preview.
- Display durable attendance success separately from a failed read-only roster refresh. No changes to SQLCipher, attendance/audit business rules, routes or UI theme; add JavaScript regression coverage.

## Unreleased — Phase 15 Excel-import confirmation safety (2026-11-03)
- A committed import requires a real native batch receipt (nonnegative createdCount and nonempty importBatchId). Malformed successful responses are labeled uncertain, not fabricated success.
- Only ambiguous bridge timeout/offline allows same-preview same-operationId retry. Definitive stale plan rejection requires new preview; in-flight and changed-plan confirms are guarded.
- The saved outcome remains independent of history refresh; existing import schema, SQLCipher, audit, UI styling and previous rows are untouched. Extend JS regressions accordingly.

## Unreleased — Phase 15 separation popup safety (2026-11-03)
- Require a genuine native separation receipt (matching PersonId, nonempty MovementId and separated=true) before claiming success; show uncertain outcome if the receipt is malformed.
- Retry the SAME operationId and preview token only after ambiguous timeout/offline; definitive native errors require a fresh preview. Preserve the five-second delay on replay.
- Guard concurrent confirmation and stale-person selection while preserving the existing SQLCipher transaction, audit, historical records and UI appearance.
- Extend the existing separation UI test with conflict, malformed receipt and lost-response retry cases; production NO-GO remains.

## Unreleased — Phase 15 version and evidence corrections (2026-11-03)
- Make GetApplicationVersion read the actual bridge assembly informational version, rather than a fixed 0.4.0. Supply the same RELEASE_VERSION to x86/x64 published assemblies and package metadata; fail packaging on mismatched published DLL product versions.
- Add an authenticated bridge regression test for the displayed version. Repair five README links to local repository documents.
- Refresh the Phase 15 gate with independently checked current-main Windows CI evidence. **No native destructive restore or production GO is authorized.**

## Unreleased — Phase 15 audit/handover work (2026-11-02)
- Created an evidence-bound final GO/NO-GO register and reproducible installation, first-use, backup/recovery/migration and artifact-handover guidance.
- Added a non-destructive Phase 15 static contract regression for retired/active routes and administrator-only commands; wired into Core/Release preflight. **This does not replace functional tests or clean-machine acceptance.**
- No production business rules, SQLCipher data, attendance history, backup activation or existing UI/handler behavior were changed by this documentation/audit increment.
- Production decision remains **NO-GO** until independent clean offline Windows, cross-PC recovery, 10,000-person multi-year benchmark and owner signatures are recorded.

## PR #191 — Phase 14 partial Windows distribution candidate (merged 2026-11-02)
- Added genuine x86/x64 native PE validation for WebView2/.NET/SQLCipher.
- Strengthened verified identical-byte Portable/Installer distribution staging and SHA-256 provenance.
- Extended runner tests for Portable relocation, local WebView2 bridge and same-version Installer reinstall/uninstall with user-data preservation.
- Fixed the synthetic monthly-export regression fixture to match the current-day initial window; no product calendar rule was changed.
- Two Windows PR-head workflows succeeded and uploaded four customer-class packages and checksum metadata. **Independent clean-PC acceptance and older-to-newer upgrade were not performed.**

This is a source/project changelog, **not** a declaration of a signed production release.
