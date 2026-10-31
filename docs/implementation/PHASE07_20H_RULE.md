# Historical notice — superseded 20:00 attendance instructions

The earlier contents of this file described an **automatic attendance job at 20:00** and a multi-day chained catch-up. Those operational instructions are **withdrawn** under the owner's 2026-10-28 reconciled directive and 2026-10-31 decisions. The original text remains available in Git history for historical analysis, but is **NOT** active implementation guidance.

Binding policy: configurable default **08:00** automatic attendance; on resume, automatic catch-up covers **yesterday only** plus eligible today. Default **20:00** is a separate **manual-attendance lock**, not a second automatic job. The default status fallback is explicitly configured. Automatic execution time, lock time and fallback are persisted as **three independent**, audited saves with individual failure messages. General and Ordinary Administration may separately authorize a one-date missing-attendance catch-up within the past **seven calendar days**; Supervisor may not.

See `docs/requirements/OWNER_DECISIONS_2026-10-31.md`, `docs/requirements/REQUIREMENTS_TRACEABILITY.md`, and `docs/architecture/ADR-0001.md`. `screen_069` retains stable old `routeKey=EightPmScheduler` for navigation identity only. Runtime tests belong to Phase 07/13, not to Phase 00.
