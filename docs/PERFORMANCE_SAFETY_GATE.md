# التمام اليومي — Mandatory Cross-Phase Safety & Performance Gate

This contract applies to ALL implementation, maintenance and performance phases, including future AI contributions.

## Three independent acceptance gates — all are mandatory

1. **Measured improvement.** Capture a reproducible BEFORE baseline and AFTER measurement using the same schema, synthetic fixtures, build configuration and hardware class. Report elapsed timings AND number of SQL calls/query plans or resource consumption as applicable. If a proposed optimization does not show a credible measured gain, keep the existing implementation. No optimization is accepted merely because a code pattern looks cleaner or fashionable.
2. **Output parity.** Assert exact business-result parity for dates, statuses, transfer-effective dates, site memberships, summaries, roster order, paging, monthly grids, Excel/report outputs, audit events, user-visible error messages and historical snapshots. If outputs legitimately change because of an authorized feature, this must be explicit and separately tested. Silent behavioral change is a rejection.
3. **Security, integrity and regression tests.** Windows restore/build, all applicable tests, SQLCipher integrity/migration and backup, concurrency, cancellation, role authorization, session revocation, anti-replay, transactional rollback, idempotency, historic-day closure and durable audit/evidence assertions MUST pass on the exact tested Git commit. No failed/skipped required checks, and do not merge while CI is pending.

For each change, record base SHA, changed files, reproducible benchmark evidence, output-parity tests, safety tests and final Windows CI run. Test against production-compatible schema and 100/1,000/2,000/10,000-person synthetic loads where feasible. Real personal records must not be used. Create PRs on separate branches; only merge after all gates pass.

## Preview-token retention

- Tokens remain valid only within their existing short-lived approval window. Cleanup MUST NOT alter that validity.
- An expired token may be removed only after MORE than 30 complete days have passed since expiry, and only if it is not referenced by another current or historical preview token or anywhere else in the database. Consumed or unconsumed status ALONE is not deletion authorization.
- Never delete AuditEvents, AuditLogPresentationEdits, AttendanceRecords, AttendanceRevisions, PersonnelMovements, AutoAttendanceRuns, MutationReceipts, identity/site/status history, exports or any other business evidence as part of preview-token maintenance.
- Fail closed on reference-check or database errors; keep SQLCipher transactions, cancellation and bounded maintenance work.

## Explicitly deferred until the full application is finished

Reassess only after the application is functionally complete, with the same three acceptance gates:
- Dashboard summary caching / 30-second TTL and proper invalidation.
- General bridge rate limiting.
- General-purpose timer-based cleanup jobs and purging any ledger entries (historical or audit deletion remains prohibited).
- Speculative shared-cache WAL tuning, connection-pool setting changes, service-singleton refactors and other micro-optimizations.
- Replacing already indexed correlated dashboard/monthly queries without proof from EXPLAIN QUERY PLAN and timed output-parity benchmarks.

These deferred changes MUST NOT be enabled prematurely. Current essential behavior, permissions and user-requested features take precedence over unverified speed claims.
