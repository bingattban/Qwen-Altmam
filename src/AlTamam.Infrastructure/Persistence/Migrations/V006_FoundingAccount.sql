-- Section 03 founding administrator. Previous databases upgrade without changing
-- stored usernames, password hashes, PersonIds or audit records.
ALTER TABLE Users ADD COLUMN FoundingFullName TEXT NULL;
-- No second GeneralAdmin through alternate service, direct SQL or a replayed setup.
CREATE TRIGGER IF NOT EXISTS TR_Users_NoSecondGeneralAdmin_Insert
BEFORE INSERT ON Users
WHEN NEW.Role=1 AND EXISTS (SELECT 1 FROM Users WHERE Role=1)
BEGIN
  SELECT RAISE(ABORT,'A founding GeneralAdmin already exists');
END;
CREATE TRIGGER IF NOT EXISTS TR_Users_NoSecondGeneralAdmin_Update
BEFORE UPDATE OF Role ON Users
WHEN NEW.Role=1 AND OLD.Role<>1
BEGIN
  SELECT RAISE(ABORT,'Role promotion to founding GeneralAdmin is disabled');
END;
