# Phase 06 — attendance baseline reconciliation, 2026-11-01

Compared owner requirements 06.01–06.13 to the actual main repository.

Existing, retained: one-per-day SQLCipher unique constraint, authenticated bridge
and today-only cutoffs, mandatory one-time preview, atomic 100-person batch
commit, session-owned drafts, append-only change audit, unchanged auto-08:00
source marker, read-only automatically prepared list with a separate
review/confirm action, paged daily and dashboard rosters, indexed daily
aggregation, site/status counters, and no retired Not Prepared/Completion
dashboard cards.

Additional acceptance coverage introduced in this phase:
- Preview and stale commit report **every** conflicting selected person, with
  stable identity, actual name and number (A). Read exit/transfer dates only
  from SQLCipher PersonnelMovements; no inferred reasons or silent partial save.
  Global closure errors enumerate actual selected identities where provided.
- Attendance status options and read-only roster status badges reflect the
  defined status hex colors, while preserving keyboard-native selectors.
- A synthetic encrypted **1,000-person** test validates paging, 100-person
  atomic Absence batch, zero phantom writes on preview, lost-response replay,
  and unprepared counts (Absent is committed attendance). A mixed
  prepared/transfer-day rejection test asserts both names and the real date.

The backend still authorizes all changes and revalidates one-use token,
assignment, original status/revision, and day closure inside the write
transaction. Phase 11 remains owner of exceptional closed-day corrections.
This phase does **not** claim future reporting/export or physical Windows UAT.

The indexed dashboard grouping delegates final saved/unprepared/transfer
arithmetic to `src/AlTamam.Application/Dashboard/DashboardStrengthSummary.cs`.
A synthetic SQLCipher trigger forces the second write in a two-person batch
to fail and proves the first attendance, audit and receipt all roll back.
