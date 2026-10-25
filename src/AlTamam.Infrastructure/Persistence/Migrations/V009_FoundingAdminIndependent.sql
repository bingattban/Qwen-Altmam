-- V009 / OWNER-APPROVED 2026-10-25 / Section 03.
-- A founding GeneralAdministration account is independent of the personnel
-- roster forever. FoundingFullName is optional; never assign a PersonId or A
-- service number. Previously linked founder accounts are detached without
-- changing UserId, Username, password hashes, role, activation state, creator
-- fields, immutable attendance or existing audit evidence.
UPDATE Users SET LinkedPersonId=NULL, RowVersion=RowVersion+1
WHERE Role=1 AND LinkedPersonId IS NOT NULL;

-- Defense in depth: enforce at the SQLCipher boundary even if the UI/Bridge
-- is bypassed, or a future API attempts to link/re-role an account.
CREATE TRIGGER IF NOT EXISTS TR_Users_FoundingIndependent_Insert
BEFORE INSERT ON Users
WHEN NEW.Role=1 AND NEW.LinkedPersonId IS NOT NULL
BEGIN
  SELECT RAISE(ABORT,'Founding GeneralAdmin must not link to a personnel record');
END;

CREATE TRIGGER IF NOT EXISTS TR_Users_FoundingIndependent_Update
BEFORE UPDATE OF Role,LinkedPersonId ON Users
WHEN NEW.Role=1 AND NEW.LinkedPersonId IS NOT NULL
BEGIN
  SELECT RAISE(ABORT,'Founding GeneralAdmin must not link to a personnel record');
END;
