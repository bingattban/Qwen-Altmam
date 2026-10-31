# Phase 05 — roster, archived identity and catalogs reconciliation

Compared owner 05.01–05.14 to main before changes. Existing authentic
PersonRosterService/ArchivedReturnService/AdminConfigurationService and
SQLCipher migration V001–V011 already implement paged active search, immutable
PersonId, half-open identity/assignment windows, explicit same-human return,
archived-name disambiguation, dismissed-only different-human number reuse,
active-site protection, status symbol/name checks, archive audit and snapshots.

## Newly closed gaps

- Every catalog mutation also explicitly checks the recognized native role at the C# AdminConfigurationService boundary using src/AlTamam.Application/Catalogs/CatalogMutationPolicy.cs; unknown roles cannot write sites or status definitions.
- The production bridge now routes sensitive roster edits through
  `RosterAuthorizedCommands` (src/AlTamam.Application/Roster/). The C# service
  entry checks the authenticated actor against the currently active SQLCipher
  user row and role, independently of the JS navigation matrix. Archiving
  remains exclusive to GeneralAdministration / OrdinaryAdministration.
- A forged first-admission date on the bridge cannot backdate or future-date
  new people. Trusted historical fixture and Excel import paths retain their
  independent temporal semantics and are not rewritten.
- Active-number collisions now identify the currently active PersonId and
  visible name; no duplicate row is written.
- Edit Names retains the existing sole Add Person modal (screen_070) and
  adds selection with a 100-person cap, complete batch preview, explicit reason
  and ONE atomic ArchivePersons commit; individual removal stays unchanged.
- Existing tests for archived return, dismissal reuse, external transfer,
  month-15 blank dates, site retirement and statuses remain required alongside
  new product-gateway security/duplicate and JS bulk UI tests.

## Explicit boundaries

No SQLCipher provider replacement. No new top-level screen. Report/archive
rendering remains owned by Phases 08 and 09, and automated Windows E2E is
not a clean-machine manual acceptance result. The original Bridge service
methods remain for trusted internal fixture and import support, NOT the
production WebView2 mutation path. Never silently reuse externally transferred
numbers or assume a matching name proves the same human.
