-- Section 02 / owner decision, 2026-10-24:
-- Immutable, non-recyclable technical identifiers never appear as roster numbers.
-- Personnel names and service numbers are editable snapshots; technical IDs never are.
CREATE TRIGGER IF NOT EXISTS TR_Persons_PermanentId
BEFORE UPDATE OF PersonId ON Persons
WHEN NEW.PersonId <> OLD.PersonId
BEGIN SELECT RAISE(ABORT,'PersonId is permanent'); END;
CREATE TRIGGER IF NOT EXISTS TR_Persons_NoPhysicalDeletion
BEFORE DELETE ON Persons
BEGIN SELECT RAISE(ABORT,'Preserve personnel history: close lifecycle instead'); END;
CREATE TRIGGER IF NOT EXISTS TR_Sites_PermanentId
BEFORE UPDATE OF SiteId ON Sites
WHEN NEW.SiteId <> OLD.SiteId
BEGIN SELECT RAISE(ABORT,'SiteId is permanent'); END;
CREATE TRIGGER IF NOT EXISTS TR_Sites_NoPhysicalDeletion
BEFORE DELETE ON Sites
BEGIN SELECT RAISE(ABORT,'Preserve site history: deactivate and reassign'); END;
CREATE TRIGGER IF NOT EXISTS TR_StatusDefinitions_PermanentId
BEFORE UPDATE OF StatusId ON AttendanceStatusDefinitions
WHEN NEW.StatusId <> OLD.StatusId
BEGIN SELECT RAISE(ABORT,'StatusId is permanent'); END;
CREATE TRIGGER IF NOT EXISTS TR_StatusDefinitions_NoPhysicalDeletion
BEFORE DELETE ON AttendanceStatusDefinitions
BEGIN SELECT RAISE(ABORT,'Preserve status history: deactivate instead'); END;
CREATE TRIGGER IF NOT EXISTS TR_Users_PermanentId
BEFORE UPDATE OF UserId ON Users
WHEN NEW.UserId <> OLD.UserId
BEGIN SELECT RAISE(ABORT,'Account UserId is permanent'); END;
CREATE TRIGGER IF NOT EXISTS TR_PersonAssignments_PermanentId
BEFORE UPDATE OF AssignmentId ON PersonAssignments
WHEN NEW.AssignmentId <> OLD.AssignmentId
BEGIN SELECT RAISE(ABORT,'AssignmentId is permanent'); END;
