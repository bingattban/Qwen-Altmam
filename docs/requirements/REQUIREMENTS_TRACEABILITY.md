# Requirements traceability (Phase 00; verified against the canonical 69-route tree)
Source: owner-supplied English implementation directive, Sections 04–07; OWNER_DECISIONS_2026-10-24.md overrides conflicting older text.

## Boundary
This inventory does **not** assert that a screen's interactions are implemented. The file `SCREEN_MATRIX.csv` is generated from the route manifest at this commit, with stable screen IDs 001–019, 021–070; screen_020 is retired permanently.
The same route inventory is independently checked against the shipping JavaScript SCREEN_CATALOG by `build/validate-ui-shell.py` on both Windows PR workflows. screen_025 is the **only** report; screen_070 is an embedded one-person Add Person flow owned by Phase 05. No report alias or separate report screen_020 is allowed.

## Original 00–15 ownership
| Phase | Contract |
|---|---|
| 00 | Requirements, unique route IDs/paths, stable manifest, dependencies and edge-case inventory |
| 01 | .NET WPF foundation, offline packaging prerequisites, reproducible Windows CI |
| 02 | SQLCipher, immutable IDs, temporal history, migration safety and recoverability |
| 03 | Founding administrator, Argon2id, linked user accounts, RBAC, inactivity and drafts |
| 04 | Real WebView2 integration, route navigation, typed C# bridge |
| 05 | Roster/add/return/status/site management |
| 06 | Attendance and dashboard counts |
| 07 | Optional 20:00 source-labeled completion and day closing |
| 08 | Internal/external moves, dismissal and monthly grid |
| 09 | Single individual/group historical report and Excel export |
| 10 | Excel roster imports/exports and dynamic column structure |
| 11 | Append-only audit display revisions and closed-day correction |
| 12 | Scheduled backup, validated restore, cross-machine recovery and reset |
| 13 | Real Windows UI/E2E, accessibility, negative tests and performance |
| 14 | Offline clean-machine acceptance of Portable and Installer |
| 15 | Final audit, release documentation and GO/NO-GO |

## Owner-approved overrides
- Personnel No. (ع) is **text, four or more digits**, retains zeros and normalizes Arabic/Persian numerals.
- Founding GeneralAdmin is single-instance, may omit full name and can **never** link to roster PersonId (V009 SQL guard). Other users require real active roster membership.
- Preferred backup/export path E:\\AlTamamAlYawmi\\Backups **only if writable**; otherwise require a visible Windows folder choice.
- Every proposed change uses PR and both independent Windows Core and Windows Release checks on the **same head SHA**. Release tests pass before Portable ZIP and Installer EXE upload; never merge with a failed gate.
- No other business-rule contradiction is silently resolved: refer to the owner.

## Mandatory failure cases mapped to phases
- Active/archived duplicate identity, ambiguous return and first-admission blank dates — 02, 05.
- Lost DPAPI key, wrong SQLCipher key, missing disk, interrupted restore or migration — 02, 12.
- Revoked session during an in-flight write and 299/300-second idle — 03, 13.
- Closed-day correction Supervisor denial and append-only original audit — 03, 11.
- Retired duplicate report and group PersonId independence — 00, 09.
- Large 10,000-person output, cancellation, Excel injection and storage capacity — 10, 13.

## Current state
Passing static route inventory is **not** a claim of Phase 04+ acceptance. Phase 02 requires verified pre-migration rescue for existing encrypted databases, and Phase 03 requires cancellation/commit-boundary tests for writes still running during logout. Manual Windows GUI/end-user testing is not replaced by CI green checks.
