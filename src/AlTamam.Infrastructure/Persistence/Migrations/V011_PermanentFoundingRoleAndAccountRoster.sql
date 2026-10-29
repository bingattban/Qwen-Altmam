-- V011 / Section 03.02-03.04 and 03.16-03.17.
-- Forward-only protection for account identity. Never modify V001-V010,
-- retroactively rewrite existing accounts, or change historical audit evidence.
--
-- The founding GeneralAdmin can never be demoted and then assigned a
-- personnel link. V009 already detaches previously linked founding records.
CREATE TRIGGER IF NOT EXISTS TR_Users_FoundingRoleImmutable_Update
BEFORE UPDATE OF Role ON Users
WHEN OLD.Role=1 AND NEW.Role<>1
BEGIN
  SELECT RAISE(ABORT,'The founding GeneralAdmin role cannot be reassigned');
END;

-- Every NEW nonfounder, including a new disabled account, must be linked
-- to a real active roster person. Existing disabled legacy rows are untouched.
-- Inactive legacy account rows remain available for audit/reconciliation.
CREATE TRIGGER IF NOT EXISTS TR_Users_ActiveNonfounderRoster_Insert
BEFORE INSERT ON Users
WHEN NEW.Role<>1 AND
  (NEW.LinkedPersonId IS NULL OR NOT EXISTS(
    SELECT 1 FROM Persons p WHERE p.PersonId=NEW.LinkedPersonId
      AND p.ActiveFlag=1 AND p.OrganizationalStatus=1))
BEGIN
  SELECT RAISE(ABORT,'An active nonfounder must link to an active roster person');
END;

CREATE TRIGGER IF NOT EXISTS TR_Users_ActiveNonfounderRoster_Update
BEFORE UPDATE OF Role,LinkedPersonId,ActiveFlag ON Users
WHEN NEW.Role<>1 AND NEW.ActiveFlag=1 AND
  (NEW.LinkedPersonId IS NULL OR NOT EXISTS(
    SELECT 1 FROM Persons p WHERE p.PersonId=NEW.LinkedPersonId
      AND p.ActiveFlag=1 AND p.OrganizationalStatus=1))
BEGIN
  SELECT RAISE(ABORT,'An active nonfounder must link to an active roster person');
END;
