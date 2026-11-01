# Phase 12 backup retention and pre-reset rescue (partial acceptance)

Retention is opt-in through the existing UpdateBackupSchedule action and existing
AppSettings. A count of 0 (default) preserves every automatic backup; a value of
2–365 keeps at least that many SHA-256-verified automatic backups in the *currently*
selected writable folder, after a newly created encrypted backup has succeeded.
Manual files, rescue files, other folders, unregistered files, wrong digests and
reparse points are never pruned. Pruned catalog entries retain an audit event
and their prior evidence; VerificationPassed is cleared because the file is gone.
File deletion and SQL registry updates are not an atomic cross-filesystem transaction;
if a crash strikes between these operations, the historic entry may refer to a
missing file, which always fails subsequent inspection. The new valid backup
is never rolled back because retention cleanup failed.

GeneralAdmin full-reset now performs fresh password, preview version and
countdown checks, creates and verifies a WAL-consistent encrypted rescue under
%LOCALAPPDATA%/AlTamam/backups before starting the destructive SQL transaction,
and repeats authorization/version checks inside the transaction. An unavailable
internal backup location prevents reset. The internal rescue is deliberately not
registered as an externally deletable manual backup.

RESTORE remains read-only in the bridge. Native final confirmation, full quiescence,
physical power-loss/disk-full trials and independently verified cross-PC recovery
remain unmet. Phase 12 MUST remain PARTIAL until those gates succeed.
