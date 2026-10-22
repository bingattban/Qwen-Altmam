-- =============================================================================
-- AlTamam AlYawmi ("التمام اليومي") — Migration V002_MigrationHardening
-- Adds the migration-harden columns required by the versioned migration runner:
--   * SchemaVersions.Sha256  — immutable SHA-256 pin of each applied migration body
--   * SchemaVersions.BackupPath — optional path of the pre-migration backup file
-- This migration is FORWARD-ONLY. V001_InitialSchema.sql is NOT rewritten in place.
-- Idempotent guards allow databases already carrying these columns to upgrade safely.
-- =============================================================================

PRAGMA foreign_keys = ON;

ALTER TABLE SchemaVersions ADD COLUMN Sha256 TEXT NULL;
ALTER TABLE SchemaVersions ADD COLUMN BackupPath TEXT NULL;

CREATE INDEX IF NOT EXISTS IX_SchemaVersions_Version_Desc
    ON SchemaVersions(SchemaVersion DESC);
