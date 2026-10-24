-- V008: durable, encrypted, transaction-atomic operation receipts.
-- A retry must return the original creation result without issuing a second
-- person, site, status or account ID and without duplicating the audit.
CREATE TABLE IF NOT EXISTS MutationReceipts(
  ActorUserId TEXT NOT NULL,
  OperationId TEXT NOT NULL,
  ActionName TEXT NOT NULL,
  PayloadSha256 TEXT NOT NULL,
  ResultJson TEXT NOT NULL,
  RecordedAtUtc TEXT NOT NULL,
  PRIMARY KEY (ActorUserId, OperationId),
  CHECK(length(OperationId) BETWEEN 16 AND 128),
  CHECK(length(PayloadSha256)=64)
);
CREATE INDEX IF NOT EXISTS IX_MutationReceipts_CreatedAt ON MutationReceipts(RecordedAtUtc);
