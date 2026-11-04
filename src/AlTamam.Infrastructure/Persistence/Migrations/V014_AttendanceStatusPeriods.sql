-- Forward-only V014: mandatory date bounds for the two canonical absence/permission
-- statuses. Older migration bodies and their hash-pinned evidence remain immutable.
-- This table is entirely separate from daily AttendanceRecords (one row/person/day).
CREATE TABLE AttendanceStatusPeriods (
    PeriodId TEXT PRIMARY KEY,
    PersonId TEXT NOT NULL REFERENCES Persons(PersonId),
    StatusId TEXT NOT NULL REFERENCES AttendanceStatusDefinitions(StatusId)
        CHECK(StatusId IN ('STS-LEAVE','STS-PERMISSION')),
    FromLocalDate TEXT NOT NULL,
    ToLocalDate TEXT NOT NULL,
    CancelledFromLocalDate TEXT NULL,
    CreatedAtUtc TEXT NOT NULL,
    CreatedByUserId TEXT NOT NULL,
    CorrelationId TEXT NOT NULL,
    CHECK(FromLocalDate <= ToLocalDate),
    CHECK(CancelledFromLocalDate IS NULL OR CancelledFromLocalDate >= FromLocalDate)
);

CREATE INDEX IX_AttendanceStatusPeriods_Effective
    ON AttendanceStatusPeriods(FromLocalDate, ToLocalDate, PersonId);
CREATE INDEX IX_AttendanceStatusPeriods_Person
    ON AttendanceStatusPeriods(PersonId, FromLocalDate, ToLocalDate);

-- A day cannot be governed by two competing period instructions, even if code
-- bypasses the bridge. Cancellation is effective at the *start* of the day.
CREATE TRIGGER TR_AttendanceStatusPeriods_NoOverlap_Insert
BEFORE INSERT ON AttendanceStatusPeriods
WHEN EXISTS (
    SELECT 1 FROM AttendanceStatusPeriods old
    WHERE old.PersonId = NEW.PersonId
      AND MAX(old.FromLocalDate,NEW.FromLocalDate) <= MIN(old.ToLocalDate,NEW.ToLocalDate)
      AND (old.CancelledFromLocalDate IS NULL
           OR MAX(old.FromLocalDate,NEW.FromLocalDate) < old.CancelledFromLocalDate)
      AND (NEW.CancelledFromLocalDate IS NULL
           OR MAX(old.FromLocalDate,NEW.FromLocalDate) < NEW.CancelledFromLocalDate)
)
BEGIN
    SELECT RAISE(ABORT, 'Overlapping attendance status periods');
END;

CREATE TRIGGER TR_AttendanceStatusPeriods_NoOverlap_Update
BEFORE UPDATE OF PersonId,FromLocalDate,ToLocalDate,CancelledFromLocalDate
ON AttendanceStatusPeriods
WHEN EXISTS (
    SELECT 1 FROM AttendanceStatusPeriods old
    WHERE old.PeriodId <> NEW.PeriodId AND old.PersonId = NEW.PersonId
      AND MAX(old.FromLocalDate,NEW.FromLocalDate) <= MIN(old.ToLocalDate,NEW.ToLocalDate)
      AND (old.CancelledFromLocalDate IS NULL
           OR MAX(old.FromLocalDate,NEW.FromLocalDate) < old.CancelledFromLocalDate)
      AND (NEW.CancelledFromLocalDate IS NULL
           OR MAX(old.FromLocalDate,NEW.FromLocalDate) < NEW.CancelledFromLocalDate)
)
BEGIN
    SELECT RAISE(ABORT, 'Overlapping attendance status periods');
END;

-- Preserve draft periods with the original draft owner; keep older drafts valid.
ALTER TABLE AttendanceDrafts ADD COLUMN PeriodFromLocalDate TEXT NULL;
ALTER TABLE AttendanceDrafts ADD COLUMN PeriodToLocalDate TEXT NULL;
