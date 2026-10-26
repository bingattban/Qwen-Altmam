# Phase 12.06 — Encrypted restore staging / rescue (partial)

The non-destructive `EncryptedRestoreStagingService` uses the already
validated SQLCipher-keyed `AlTamamDbContext`:

1. Reject corrupt, wrongly keyed and schema-incompatible selected backups
   **before** touching the current live database.
2. Create a WAL-consistent, SQLCipher-encrypted verified RESCUE snapshot of
   the CURRENT database in the application-owned `backups/` directory.
   Never delete the previous valid backup on a staging failure.
3. Copy the selected encrypted candidate into a unique, same-filesystem
   stage file. Flush it, reopen under SQLCipher, verify integrity, foreign
   keys, version, row counts and SHA-256 against the source selection.
4. Return exact stage/rescue paths and checksums for a future explicit
   authenticated activation. No historical records are rewritten.

**This stage never switches, deletes, replaces or upgrades the live DB.**

**NOT IMPLEMENTED / NO-GO:** authenticated user confirmation, fresh password,
safe WAL connection shutdown, atomic reversible filesystem switch, durable
crash-recovery intent, startup reconciliation and power-loss tests. Those
must be implemented and accepted before the existing
`RestoreDatabaseAtomically` action can be enabled.

Windows CI tests cover different historical contents between current and
selected backup, consistent encrypted rescue, wrong SQLCipher key and old
schema rejection. They are not a substitute for two-machine manual testing.
