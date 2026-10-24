-- Forward-only hardening for the Section 02 temporal/audit invariants.
-- Do not edit already released migrations V001-V003: existing encrypted databases upgrade in place.
-- History intervals are inclusive at start and EXCLUSIVE at end.

CREATE TRIGGER IF NOT EXISTS TR_PersonIdentityHistory_Insert
BEFORE INSERT ON PersonIdentityHistory
BEGIN
    SELECT RAISE(ABORT, 'Invalid identity history interval')
      WHERE NEW.ValidToLocalDate IS NOT NULL AND NEW.ValidToLocalDate <= NEW.ValidFromLocalDate;
    SELECT RAISE(ABORT, 'Overlapping person identity history')
      WHERE EXISTS (
        SELECT 1 FROM PersonIdentityHistory h WHERE h.PersonId = NEW.PersonId
          AND NEW.ValidFromLocalDate < COALESCE(h.ValidToLocalDate, '9999-12-31')
          AND h.ValidFromLocalDate < COALESCE(NEW.ValidToLocalDate, '9999-12-31')
      );
END;

-- Prior identity values are evidence: only allow closing the currently open interval.
CREATE TRIGGER IF NOT EXISTS TR_PersonIdentityHistory_Update
BEFORE UPDATE ON PersonIdentityHistory
BEGIN
    SELECT RAISE(ABORT, 'Identity history is immutable except for closing an open interval')
      WHERE OLD.ValidToLocalDate IS NOT NULL OR NEW.ValidToLocalDate IS NULL
        OR NEW.ValidToLocalDate <= OLD.ValidFromLocalDate
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
CREATE TRIGGER IF NOT EXISTS TR_PersonIdentityHistory_Delete
BEFORE DELETE ON PersonIdentityHistory
BEGIN
    SELECT RAISE(ABORT, 'Identity history cannot be deleted');
END;

-- Preserve the protected system location even if a bridge guard is bypassed.
CREATE TRIGGER IF NOT EXISTS TR_Sites_ProtectUnspecified_Update
BEFORE UPDATE ON Sites WHEN OLD.SiteId = 'SITE-UNSPECIFIED'
BEGIN
    SELECT RAISE(ABORT, 'The Unspecified system site is immutable');
END;
CREATE TRIGGER IF NOT EXISTS TR_Sites_ProtectUnspecified_Delete
BEFORE DELETE ON Sites WHEN OLD.SiteId = 'SITE-UNSPECIFIED'
BEGIN
    SELECT RAISE(ABORT, 'The Unspecified system site cannot be deleted');
END;

-- Canonical operations and corrections are append-only; visible audit edits use their own table.
CREATE TRIGGER IF NOT EXISTS TR_AuditEvents_ImmutableUpdate
BEFORE UPDATE ON AuditEvents
BEGIN
    SELECT RAISE(ABORT, 'Canonical audit events are append-only');
END;
CREATE TRIGGER IF NOT EXISTS TR_AuditEvents_ImmutableDelete
BEFORE DELETE ON AuditEvents
BEGIN
    SELECT RAISE(ABORT, 'Canonical audit events cannot be deleted');
END;
CREATE TRIGGER IF NOT EXISTS TR_AttendanceRevisions_ImmutableUpdate
BEFORE UPDATE ON AttendanceRevisions
BEGIN
    SELECT RAISE(ABORT, 'Attendance corrections are append-only');
END;
CREATE TRIGGER IF NOT EXISTS TR_AttendanceRevisions_ImmutableDelete
BEFORE DELETE ON AttendanceRevisions
BEGIN
    SELECT RAISE(ABORT, 'Attendance corrections cannot be deleted');
END;
CREATE TRIGGER IF NOT EXISTS TR_PersonnelMovements_ImmutableUpdate
BEFORE UPDATE ON PersonnelMovements
BEGIN
    SELECT RAISE(ABORT, 'Movement history is append-only');
END;
CREATE TRIGGER IF NOT EXISTS TR_PersonnelMovements_ImmutableDelete
BEFORE DELETE ON PersonnelMovements
BEGIN
    SELECT RAISE(ABORT, 'Movement history cannot be deleted');
END;

-- Site and status labels have their own effective-dated histories. Close-only updates
-- preserve the previous display versions used by historical reports.
CREATE TRIGGER IF NOT EXISTS TR_SiteNameHistory_Insert
BEFORE INSERT ON SiteNameHistory
BEGIN
    SELECT RAISE(ABORT, 'Invalid site name history interval')
      WHERE NEW.ValidToLocalDate IS NOT NULL AND NEW.ValidToLocalDate <= NEW.ValidFromLocalDate;
    SELECT RAISE(ABORT, 'Overlapping site name history')
      WHERE EXISTS (
        SELECT 1 FROM SiteNameHistory h WHERE h.SiteId = NEW.SiteId
          AND NEW.ValidFromLocalDate < COALESCE(h.ValidToLocalDate, '9999-12-31')
          AND h.ValidFromLocalDate < COALESCE(NEW.ValidToLocalDate, '9999-12-31')
      );
END;
CREATE TRIGGER IF NOT EXISTS TR_SiteNameHistory_Update
BEFORE UPDATE ON SiteNameHistory
BEGIN
    SELECT RAISE(ABORT, 'Site name history can only close an open interval')
      WHERE OLD.ValidToLocalDate IS NOT NULL OR NEW.ValidToLocalDate IS NULL
        OR NEW.ValidToLocalDate <= OLD.ValidFromLocalDate
        OR NEW.SiteId <> OLD.SiteId OR NEW.VisibleNameSnapshot <> OLD.VisibleNameSnapshot
        OR NEW.ValidFromLocalDate <> OLD.ValidFromLocalDate
        OR NEW.ChangedByUserId <> OLD.ChangedByUserId OR NEW.RecordedAtUtc <> OLD.RecordedAtUtc;
    SELECT RAISE(ABORT, 'Overlapping site name history')
      WHERE EXISTS (
        SELECT 1 FROM SiteNameHistory h WHERE h.SiteId = NEW.SiteId AND h.HistoryId <> OLD.HistoryId
          AND NEW.ValidFromLocalDate < COALESCE(h.ValidToLocalDate, '9999-12-31')
          AND h.ValidFromLocalDate < NEW.ValidToLocalDate
      );
END;
CREATE TRIGGER IF NOT EXISTS TR_SiteNameHistory_Delete
BEFORE DELETE ON SiteNameHistory
BEGIN
    SELECT RAISE(ABORT, 'Site name history cannot be deleted');
END;

CREATE TRIGGER IF NOT EXISTS TR_AttendanceStatusNameHistory_Insert
BEFORE INSERT ON AttendanceStatusNameHistory
BEGIN
    SELECT RAISE(ABORT, 'Invalid status name history interval')
      WHERE NEW.ValidToLocalDate IS NOT NULL AND NEW.ValidToLocalDate <= NEW.ValidFromLocalDate;
    SELECT RAISE(ABORT, 'Overlapping status name history')
      WHERE EXISTS (
        SELECT 1 FROM AttendanceStatusNameHistory h WHERE h.StatusId = NEW.StatusId
          AND NEW.ValidFromLocalDate < COALESCE(h.ValidToLocalDate, '9999-12-31')
          AND h.ValidFromLocalDate < COALESCE(NEW.ValidToLocalDate, '9999-12-31')
      );
END;
CREATE TRIGGER IF NOT EXISTS TR_AttendanceStatusNameHistory_Update
BEFORE UPDATE ON AttendanceStatusNameHistory
BEGIN
    SELECT RAISE(ABORT, 'Status name history can only close an open interval')
      WHERE OLD.ValidToLocalDate IS NOT NULL OR NEW.ValidToLocalDate IS NULL
        OR NEW.ValidToLocalDate <= OLD.ValidFromLocalDate
        OR NEW.StatusId <> OLD.StatusId OR NEW.VisibleNameSnapshot <> OLD.VisibleNameSnapshot
        OR NEW.ShortSymbolSnapshot <> OLD.ShortSymbolSnapshot OR NEW.ColorSnapshot <> OLD.ColorSnapshot
        OR NEW.ValidFromLocalDate <> OLD.ValidFromLocalDate OR NEW.RecordedAtUtc <> OLD.RecordedAtUtc;
    SELECT RAISE(ABORT, 'Overlapping status name history')
      WHERE EXISTS (
        SELECT 1 FROM AttendanceStatusNameHistory h
        WHERE h.StatusId = NEW.StatusId AND h.HistoryId <> OLD.HistoryId
          AND NEW.ValidFromLocalDate < COALESCE(h.ValidToLocalDate, '9999-12-31')
          AND h.ValidFromLocalDate < NEW.ValidToLocalDate
      );
END;
CREATE TRIGGER IF NOT EXISTS TR_AttendanceStatusNameHistory_Delete
BEFORE DELETE ON AttendanceStatusNameHistory
BEGIN
    SELECT RAISE(ABORT, 'Status name history cannot be deleted');
END;

-- Even a bypassed service cannot attach a committed attendance row to an unrelated
-- assignment, an ineligible date, or a different site's historical interval.
CREATE TRIGGER IF NOT EXISTS TR_AttendanceRecords_ValidateEligibilityInsert
BEFORE INSERT ON AttendanceRecords
BEGIN
    SELECT RAISE(ABORT, 'Attendance date outside eligible assignment')
    WHERE NOT EXISTS (
      SELECT 1 FROM PersonAssignments a JOIN Persons p ON p.PersonId = a.PersonId
      WHERE a.AssignmentId = NEW.AssignmentIdAtEffectiveDate
        AND a.PersonId = NEW.PersonId AND a.SiteId = NEW.SiteIdAtEffectiveDate
        AND NEW.AttendanceLocalDate >= p.FirstRecordedLocalDate
        AND NEW.AttendanceLocalDate >= a.ValidFromLocalDate
        AND (a.ValidToLocalDate IS NULL OR NEW.AttendanceLocalDate < a.ValidToLocalDate)
    );
END;
CREATE TRIGGER IF NOT EXISTS TR_AttendanceRecords_ValidateEligibilityUpdate
BEFORE UPDATE OF PersonId, AttendanceLocalDate, AssignmentIdAtEffectiveDate, SiteIdAtEffectiveDate
ON AttendanceRecords
BEGIN
    SELECT RAISE(ABORT, 'Attendance date outside eligible assignment')
    WHERE NOT EXISTS (
      SELECT 1 FROM PersonAssignments a JOIN Persons p ON p.PersonId = a.PersonId
      WHERE a.AssignmentId = NEW.AssignmentIdAtEffectiveDate
        AND a.PersonId = NEW.PersonId AND a.SiteId = NEW.SiteIdAtEffectiveDate
        AND NEW.AttendanceLocalDate >= p.FirstRecordedLocalDate
        AND NEW.AttendanceLocalDate >= a.ValidFromLocalDate
        AND (a.ValidToLocalDate IS NULL OR NEW.AttendanceLocalDate < a.ValidToLocalDate)
    );
END;
