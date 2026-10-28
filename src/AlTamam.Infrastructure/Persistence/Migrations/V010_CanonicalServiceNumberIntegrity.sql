-- Forward-only Section 02 personnel-number enforcement at the SQLCipher boundary.
-- Keep V001-V009 immutable: their recorded SHA-256 pins are historical evidence.
-- Service-number identifiers are CANONICAL ASCII digit strings (4..70 characters).
-- Leading zeros are meaningful; do not cast to INTEGER or trim them.
-- Legacy rows, if any, remain untouched. New writes and number changes must
-- satisfy the canonical policy; never rewrite archived identities to "repair" them.

CREATE TRIGGER IF NOT EXISTS TR_Persons_CanonicalServiceNumber_Insert
BEFORE INSERT ON Persons
BEGIN
    SELECT RAISE(ABORT, 'Personnel number must be 4..70 canonical ASCII digits')
    WHERE length(NEW.NormalizedServiceNumberA) NOT BETWEEN 4 AND 70
       OR NEW.NormalizedServiceNumberA GLOB '*[^0-9]*'
       OR NEW.CurrentServiceNumberA <> NEW.NormalizedServiceNumberA;
END;

CREATE TRIGGER IF NOT EXISTS TR_Persons_CanonicalServiceNumber_Update
BEFORE UPDATE OF CurrentServiceNumberA, NormalizedServiceNumberA ON Persons
BEGIN
    SELECT RAISE(ABORT, 'Personnel number must be 4..70 canonical ASCII digits')
    WHERE length(NEW.NormalizedServiceNumberA) NOT BETWEEN 4 AND 70
       OR NEW.NormalizedServiceNumberA GLOB '*[^0-9]*'
       OR NEW.CurrentServiceNumberA <> NEW.NormalizedServiceNumberA;
END;

-- Historical identity snapshots are never changed in place. The application
-- already converts user-entered Arabic-Indic and Persian digits to canonical
-- ASCII before inserting the next effective-dated snapshot.
CREATE TRIGGER IF NOT EXISTS TR_PersonIdentityHistory_CanonicalServiceNumber_Insert
BEFORE INSERT ON PersonIdentityHistory
BEGIN
    SELECT RAISE(ABORT, 'Historical personnel number must be 4..70 canonical ASCII digits')
    WHERE length(NEW.ServiceNumberASnapshot) NOT BETWEEN 4 AND 70
       OR NEW.ServiceNumberASnapshot GLOB '*[^0-9]*';
END;
