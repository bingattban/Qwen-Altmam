# Phase 01 — SQLCipher provider upgrade evaluation (owner decision 2026-10-31)

## Exact current baseline
- Infrastructure: `Microsoft.Data.Sqlite.Core 9.0.2` plus
  `SQLitePCLRaw.bundle_e_sqlcipher 2.1.10` from `Directory.Packages.props`.
- `AlTamamDbContext` calls `SQLitePCL.Batteries_V2.Init()`, uses a 32-byte
  existing key, `PRAGMA cipher_version`, WAL and FK enforcement, encrypted
  online backup and read-only integrity checks. The existing data root is
  `%LOCALAPPDATA%\\AlTamam` for both Portable and Installer.
- Synthetic tests already cover opening an encrypted database, invalid key
  rejection, WAL/FK enforcement, at-rest encryption, encrypted backup,
  migration rescue, staged restore and corrupted/incorrect key refusals.
  These are **baseline tests with the old native provider**; they do not prove
  a different provider is compatible.

## Verified upstream facts

1. SQLitePCLRaw's maintainer explicitly **deprecated**
   `bundle_e_sqlcipher` beginning with the 3.0 release; he no longer
   provides maintained encryption-enabled binaries for free:
   https://github.com/ericsink/SQLitePCL.raw/wiki/SQLite-encryption-options-for-use-with-SQLitePCLRaw
2. Microsoft documents `Microsoft.Data.Sqlite.Core` plus a suitable provider
   (instead of the default unencrypted bundled SQLite). SQLCipher integration:
   https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions
3. Zetetic distributes supported .NET native SQLCipher packages under a
   **commercial/enterprise license**, requiring an owner-approved license
   and package access. Do not add a trial binary to a distributable build:
   https://www.zetetic.net/sqlcipher/sqlcipher-for-dotnet/
4. SQLCipher Community Edition source is BSD-style licensed but the third-party
   pre-built bundle is unsupported/deprecated. Self-building maintained community
   native binaries is another possibility, but requires deterministic Windows
   x86/x64 toolchain, native hash verification, update maintenance and full
   redistribution notices; it has **not** been completed here:
   https://www.zetetic.net/sqlcipher/community/
5. SQLCipher Community redistribution requires its full original license
   and associated dependency notices in user-accessible distributed materials;
   commercial licensing terms differ:
   https://www.zetetic.net/sqlcipher/license/

## Owner-approved condition — MUST pass before any implementation switch

- Independently establish a **legally redistributable**, supported native
  SQLCipher candidate for BOTH real Windows x86 and x64. Record the precise
  package/native binary versions, architecture, hashes and approved licenses.
- On Windows, create **synthetic** V001 and latest-schema databases with the
  current provider, populate attendance, people, identity/history, accounts
  and audit data; generate WAL-consistent encrypted backups and an encrypted
  recovery package with the existing keys. Do not use or upload real personnel.
- With the OLD binaries closed, open the **unchanged original encrypted bytes**
  and backups using the candidate provider and the **same** key and settings.
  Verify `cipher_version`, `integrity_check`, `foreign_key_check`, schema,
  all essential row counts and immutable audit hashes. Test x86/x64, both
  older-schema migration and latest schema, wrong key, crash/WAL recovery,
  interrupted backup and disk/write failures.
- Prove backup **and restore** round trips between old and candidate providers,
  and that existing Windows DPAPI-protected keys, .altrecovery procedures and
  Portable/Installer shared AppData work without data migration or rekeying.
  Never mutate an owner's original database as part of candidate tests.
- Run actual C# and JS suites, Windows WebView2 E2E, full release test-first
  workflows, four real packages with SHA-256 and clean-machine testing.
  Do not call the migration compatible until all tests and legal checks pass.
  Any failure blocks rollout. A new provider requires a separate reviewed
  change, not a silent `Directory.Packages.props` version bump.

## Present decision / gate

**Candidate switch: BLOCKED / NOT PROVEN.** The owner authorized studying
and switching only after evidence. No Zetetic commercial entitlement, pinned
supported x86/x64 native candidate, cross-provider compatibility evidence or
clean-machine acceptance has been supplied. Retain the existing tested
SQLCipher provider without changing database format, key derivation, recovery
protocols or data paths. This is not a claim that the deprecated dependency
has been modernized; it is a recorded conditional follow-up.

**Phase 01 PR boundary:** no rename of `DesktopHost` or duplicate WebUI
project. No unrelated business/UI changes. A successful PR CI run validates
only the unchanged current provider and candidate application build.
