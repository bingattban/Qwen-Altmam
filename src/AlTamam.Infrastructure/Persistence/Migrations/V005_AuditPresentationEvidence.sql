-- SECTION 02.11: the visible presentation can be revised but the original
-- AuditEvents and every historical AuditLogPresentationEdits revision are permanent.
-- Existing databases upgrade forward; released V001-V004 resources are untouched.

CREATE UNIQUE INDEX IF NOT EXISTS UX_AuditLogPresentationEdits_Event_Version
    ON AuditLogPresentationEdits(AuditEventId, OverlayVersion);

CREATE TRIGGER IF NOT EXISTS TR_AuditLogPresentationEdits_ImmutableUpdate
BEFORE UPDATE ON AuditLogPresentationEdits
BEGIN
    SELECT RAISE(ABORT, 'Audit presentation history is append-only');
END;

CREATE TRIGGER IF NOT EXISTS TR_AuditLogPresentationEdits_ImmutableDelete
BEFORE DELETE ON AuditLogPresentationEdits
BEGIN
    SELECT RAISE(ABORT, 'Audit presentation history cannot be deleted');
END;
