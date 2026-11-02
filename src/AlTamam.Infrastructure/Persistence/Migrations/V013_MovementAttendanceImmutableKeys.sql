-- SECTION 09 / AC-008, AC-017, AC-029, AC-038: forward-only storage hardening.
-- Released V001..V012 files and their verified SHA-256 history stay immutable.
-- Movement type 4 is the owner's verified SAME-PERSON Return; distinct-person
-- dismissed-number reuse creates a NEW PersonId and is not a return event.
CREATE TRIGGER IF NOT EXISTS TR_PersonnelMovements_AllowedType_Insert
BEFORE INSERT ON PersonnelMovements
WHEN NEW.MovementType NOT IN (1,2,3,4)
BEGIN
    SELECT RAISE(ABORT,'Unsupported personnel movement type');
END;

-- A committed attendance belongs permanently to the recorded person/date;
-- approved status correction updates status/revision with separate audit evidence.
-- Do not permit a direct key rewrite even if BOTH dates have valid assignments.
CREATE TRIGGER IF NOT EXISTS TR_AttendanceRecords_ImmutablePersonDate_Update
BEFORE UPDATE OF PersonId,AttendanceLocalDate ON AttendanceRecords
WHEN NEW.PersonId <> OLD.PersonId OR NEW.AttendanceLocalDate <> OLD.AttendanceLocalDate
BEGIN
    SELECT RAISE(ABORT,'Committed attendance person/date cannot be reassigned');
END;

-- Existing interval-overlap triggers remain authoritative for ALL historical
-- periods. This complementary partial unique index prevents two open
-- assignments for the SAME PersonId at the storage/index layer.
CREATE UNIQUE INDEX IF NOT EXISTS UX_PersonAssignments_OneOpenPerPerson
ON PersonAssignments(PersonId)
WHERE ValidToLocalDate IS NULL;
