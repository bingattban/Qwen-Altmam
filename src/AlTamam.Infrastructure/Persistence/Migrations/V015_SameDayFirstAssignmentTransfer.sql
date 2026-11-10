-- V015: permit an initial assignment to close on its first day ONLY
-- when an immutable internal/external movement for that person/source/day has
-- already been written in the same SQLCipher transaction.
-- A normal zero-length interval, backdated rewrite, or an unrelated edit
-- remains forbidden. Previous hash-pinned migrations must never be modified.
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
                  AND m.MovementType IN (1, 2)
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
