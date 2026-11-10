-- V016: separately approved, forward-only admission-day separation and
-- identity correction. No historical migration (V001-V015) may be rewritten.
-- A first-day assignment may close only after the matching immutable movement
-- (internal, external, or separation) is inserted in the SAME SQLCipher tx.
DROP TRIGGER IF EXISTS TR_PersonAssignments_ValidateUpdate;
CREATE TRIGGER TR_PersonAssignments_ValidateUpdate
BEFORE UPDATE OF PersonId, ValidFromLocalDate, ValidToLocalDate
ON PersonAssignments
BEGIN
    SELECT RAISE(ABORT, 'Invalid assignment date interval')
      WHERE NEW.ValidToLocalDate IS NOT NULL
        AND NEW.ValidToLocalDate <= NEW.ValidFromLocalDate
        AND NOT (
            OLD.ValidToLocalDate IS NULL
            AND NEW.ValidToLocalDate = NEW.ValidFromLocalDate
            AND NEW.PersonId = OLD.PersonId
            AND NEW.SiteId = OLD.SiteId
            AND NEW.ValidFromLocalDate = OLD.ValidFromLocalDate
            AND OLD.ValidFromLocalDate = (
                SELECT p.FirstRecordedLocalDate FROM Persons p
                WHERE p.PersonId = OLD.PersonId)
            AND EXISTS (
                SELECT 1 FROM PersonnelMovements m
                WHERE m.PersonId = OLD.PersonId
                  AND m.SourceSiteId = OLD.SiteId
                  AND m.EffectiveLocalDate = NEW.ValidToLocalDate
                  AND m.MovementType IN (1, 2, 3)
            )
        );
    SELECT RAISE(ABORT, 'Overlapping person assignments')
      WHERE EXISTS (
          SELECT 1 FROM PersonAssignments p
          WHERE p.PersonId = NEW.PersonId AND p.AssignmentId <> OLD.AssignmentId
            AND NEW.ValidFromLocalDate < COALESCE(p.ValidToLocalDate, '9999-12-31')
            AND p.ValidFromLocalDate < COALESCE(NEW.ValidToLocalDate, '9999-12-31')
      );
END;

-- A person admitted today can have multiple corrected spellings/numbers
-- today without overwriting a single immutable identity snapshot.
-- Every prior version is closed to an empty calendar interval but retained
-- as read-only evidence, and every correction must already have a matching
-- hash-chained AuditEvents row within the SAME write transaction.
-- Prior-day identity records remain governed by the original strict rules.
DROP TRIGGER IF EXISTS TR_PersonIdentityHistory_Update;
CREATE TRIGGER TR_PersonIdentityHistory_Update
BEFORE UPDATE ON PersonIdentityHistory
BEGIN
    SELECT RAISE(ABORT, 'Identity history is immutable except for audited admission-day correction or closing an open interval')
      WHERE OLD.ValidToLocalDate IS NOT NULL OR NEW.ValidToLocalDate IS NULL
        OR NEW.ValidToLocalDate < OLD.ValidFromLocalDate
        OR (NEW.ValidToLocalDate = OLD.ValidFromLocalDate AND NOT (
            OLD.ValidFromLocalDate = (
                SELECT p.FirstRecordedLocalDate FROM Persons p
                WHERE p.PersonId = OLD.PersonId)
            AND EXISTS (
                SELECT 1 FROM AuditEvents e
                JOIN Persons p ON p.PersonId = OLD.PersonId
                WHERE e.OperationCode='PERSON_IDENTITY_UPDATE'
                  AND e.TargetEntityType='Person'
                  AND e.TargetEntityId=OLD.PersonId
                  AND e.EffectiveLocalDate=NEW.ValidToLocalDate
                  AND e.ExecutedAtUtc >= OLD.RecordedAtUtc
                  AND json_valid(e.OldValueJson) AND json_valid(e.NewValueJson)
                  AND json_extract(e.OldValueJson,'$.name')=OLD.NameSnapshot
                  AND json_extract(e.OldValueJson,'$.serviceNumberA')=OLD.ServiceNumberASnapshot
                  AND json_extract(e.NewValueJson,'$.name')=p.CurrentVisibleName
                  AND json_extract(e.NewValueJson,'$.serviceNumberA')=p.CurrentServiceNumberA
            )
        ))
        OR NEW.PersonId <> OLD.PersonId OR NEW.NameSnapshot <> OLD.NameSnapshot
        OR NEW.ServiceNumberASnapshot <> OLD.ServiceNumberASnapshot
        OR NEW.ValidFromLocalDate <> OLD.ValidFromLocalDate
        OR NEW.ChangedByUserId <> OLD.ChangedByUserId
        OR NEW.RecordedAtUtc <> OLD.RecordedAtUtc OR NEW.Reason <> OLD.Reason;
    SELECT RAISE(ABORT, 'Overlapping person identity history')
      WHERE EXISTS (
          SELECT 1 FROM PersonIdentityHistory h
          WHERE h.PersonId = NEW.PersonId AND h.HistoryId <> OLD.HistoryId
            AND NEW.ValidFromLocalDate < COALESCE(h.ValidToLocalDate, '9999-12-31')
            AND h.ValidFromLocalDate < NEW.ValidToLocalDate
      );
END;
