-- =============================================================================
-- AlTamam AlYawmi — Migration V003_PersonnelMasterData (Phase 05)
-- Adds the server-side preview-token store that makes every destructive or
-- identity-changing mutation a genuine two-step flow:
--   * Token = SHA-256 over (operation, actorUserId, payloadCanonicalJson, baseRevision)
--     plus a random component; one-time consumption; short expiry.
--   * Stale previews (base revision moved), foreign tokens (different actor or
--     operation) and expired tokens are rejected at commit time.
-- FORWARD-ONLY, idempotent guards allow re-application on databases that
-- already carry the table.
-- =============================================================================

PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS PreviewTokens (
    Token TEXT PRIMARY KEY,
    Operation TEXT NOT NULL,                 -- e.g. 'ArchivePersons', 'RemoveSiteAndReassignCurrentPersons'
    ActorUserId TEXT NOT NULL,               -- token is bound to its issuing principal
    PayloadCanonicalJson TEXT NOT NULL,      -- canonical re-hashable form of the preview inputs
    BaseRevision INTEGER NOT NULL,           -- optimistic-concurrency anchor at preview time
    SummaryJson TEXT NOT NULL,               -- human-readable preview summary shown in the UI
    IssuedAtUtc TEXT NOT NULL,
    ExpiresAtUtc TEXT NOT NULL,
    ConsumedFlag INTEGER NOT NULL DEFAULT 0 CHECK (ConsumedFlag IN (0, 1))
);

CREATE INDEX IF NOT EXISTS IX_PreviewTokens_Operation_Expiry
    ON PreviewTokens(Operation, ExpiresAtUtc);

INSERT OR IGNORE INTO SchemaVersions (SchemaVersion, MigrationName, AppliedAtUtc)
VALUES (3, 'V003_PersonnelMasterData', STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'));
