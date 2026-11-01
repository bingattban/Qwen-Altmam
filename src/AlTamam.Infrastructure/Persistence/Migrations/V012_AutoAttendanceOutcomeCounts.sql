-- Phase 07: preserve old 20:00 ledger rows and make newly recorded outcome
-- counts exact. NULL for historic rows means unknown, NOT a fabricated zero.
ALTER TABLE AutoAttendanceRuns ADD COLUMN DefaultedCount INTEGER NULL CHECK (DefaultedCount >= 0);
ALTER TABLE AutoAttendanceRuns ADD COLUMN SkippedManualCount INTEGER NULL CHECK (SkippedManualCount >= 0);
ALTER TABLE AutoAttendanceRuns ADD COLUMN FailedCount INTEGER NULL CHECK (FailedCount >= 0);
