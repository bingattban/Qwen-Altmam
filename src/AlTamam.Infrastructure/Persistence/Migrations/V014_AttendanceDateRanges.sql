-- Only the two protected system statuses support dated attendance ranges.
-- Immutable original attendance/audit evidence remains in AttendanceRecords.
CREATE TABLE IF NOT EXISTS AttendanceDateRanges (
  RangeId TEXT PRIMARY KEY,
  PersonId TEXT NOT NULL REFERENCES Persons(PersonId),
  StatusId TEXT NOT NULL REFERENCES AttendanceStatusDefinitions(StatusId)
    CHECK(StatusId IN ('STS-LEAVE','STS-PERMISSION')),
  StartLocalDate TEXT NOT NULL,
  EndLocalDate TEXT NOT NULL,
  CreatedAtUtc TEXT NOT NULL,
  CreatedByUserId TEXT NOT NULL,
  EndedAtUtc TEXT NULL,
  CHECK(EndLocalDate >= StartLocalDate)
);
CREATE INDEX IF NOT EXISTS IX_AttendanceDateRanges_PersonDates
  ON AttendanceDateRanges(PersonId,StartLocalDate,EndLocalDate);
-- One active date range per person on any given date.
CREATE TRIGGER IF NOT EXISTS TR_AttendanceDateRanges_NoOverlap
BEFORE INSERT ON AttendanceDateRanges
WHEN NEW.EndedAtUtc IS NULL
BEGIN
  SELECT RAISE(ABORT,'Overlapping attendance date ranges')
  WHERE EXISTS(
    SELECT 1 FROM AttendanceDateRanges r
    WHERE r.PersonId=NEW.PersonId AND r.EndedAtUtc IS NULL
      AND r.StartLocalDate<=NEW.EndLocalDate
      AND r.EndLocalDate>=NEW.StartLocalDate
  );
END;
