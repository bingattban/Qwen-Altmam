-- =============================================================================
-- AlTamam AlYawmi ("التمام اليومي") — Canonical Relational Database Schema V001
-- Enforces immutable historical snapshots, effective-dated site assignments,
-- append-only audit events, and administrator presentation overlays.
-- =============================================================================

PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS SchemaVersions (
    SchemaVersion INTEGER PRIMARY KEY,
    MigrationName TEXT NOT NULL,
    AppliedAtUtc TEXT NOT NULL
);

-- A. Persons (Immutable PersonId)
CREATE TABLE IF NOT EXISTS Persons (
    PersonId TEXT PRIMARY KEY,
    CurrentVisibleName TEXT NOT NULL,
    NormalizedName TEXT NOT NULL,
    CurrentServiceNumberA TEXT NOT NULL,
    NormalizedServiceNumberA TEXT NOT NULL,
    FirstRecordedLocalDate TEXT NOT NULL,
    FirstRecordedAtUtc TEXT NOT NULL,
    OrganizationalStatus INTEGER NOT NULL DEFAULT 1, -- 1=Active, 2=External, 3=Separated, 4=Archived
    ActiveFlag INTEGER NOT NULL DEFAULT 1 CHECK (ActiveFlag IN (0, 1)),
    RowVersion INTEGER NOT NULL DEFAULT 1
);

-- Unique service number among currently active persons
CREATE UNIQUE INDEX IF NOT EXISTS IX_Persons_Active_ServiceNumberA
    ON Persons(NormalizedServiceNumberA)
    WHERE ActiveFlag = 1;

CREATE INDEX IF NOT EXISTS IX_Persons_NormalizedName ON Persons(NormalizedName);
CREATE INDEX IF NOT EXISTS IX_Persons_ActiveFlag ON Persons(ActiveFlag, OrganizationalStatus);

-- B. PersonIdentityHistory (Append-only identity snapshots)
CREATE TABLE IF NOT EXISTS PersonIdentityHistory (
    HistoryId INTEGER PRIMARY KEY AUTOINCREMENT,
    PersonId TEXT NOT NULL REFERENCES Persons(PersonId),
    NameSnapshot TEXT NOT NULL,
    ServiceNumberASnapshot TEXT NOT NULL,
    ValidFromLocalDate TEXT NOT NULL,
    ValidToLocalDate TEXT NULL,
    ChangedByUserId TEXT NOT NULL,
    RecordedAtUtc TEXT NOT NULL,
    Reason TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_PersonIdentityHistory_Person_Dates
    ON PersonIdentityHistory(PersonId, ValidFromLocalDate, ValidToLocalDate);

-- C. Sites (Immutable SiteId; "غير محدد" seeded as protected system site)
CREATE TABLE IF NOT EXISTS Sites (
    SiteId TEXT PRIMARY KEY,
    VisibleName TEXT NOT NULL,
    NormalizedName TEXT NOT NULL,
    IsSystemSite INTEGER NOT NULL DEFAULT 0 CHECK (IsSystemSite IN (0, 1)),
    ActiveFlag INTEGER NOT NULL DEFAULT 1 CHECK (ActiveFlag IN (0, 1)),
    CreatedAtUtc TEXT NOT NULL,
    RowVersion INTEGER NOT NULL DEFAULT 1
);

CREATE UNIQUE INDEX IF NOT EXISTS IX_Sites_Active_NormalizedName
    ON Sites(NormalizedName)
    WHERE ActiveFlag = 1;

-- D. SiteNameHistory
CREATE TABLE IF NOT EXISTS SiteNameHistory (
    HistoryId INTEGER PRIMARY KEY AUTOINCREMENT,
    SiteId TEXT NOT NULL REFERENCES Sites(SiteId),
    VisibleNameSnapshot TEXT NOT NULL,
    ValidFromLocalDate TEXT NOT NULL,
    ValidToLocalDate TEXT NULL,
    ChangedByUserId TEXT NOT NULL,
    RecordedAtUtc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_SiteNameHistory_Site_Dates
    ON SiteNameHistory(SiteId, ValidFromLocalDate, ValidToLocalDate);

-- E. PersonAssignments (Effective-dated site membership)
CREATE TABLE IF NOT EXISTS PersonAssignments (
    AssignmentId TEXT PRIMARY KEY,
    PersonId TEXT NOT NULL REFERENCES Persons(PersonId),
    SiteId TEXT NOT NULL REFERENCES Sites(SiteId),
    ValidFromLocalDate TEXT NOT NULL,
    ValidToLocalDate TEXT NULL, -- Exclusive end date (NULL = currently active assignment)
    SourceMovementId TEXT NULL,
    CreatedAtUtc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_PersonAssignments_Person_Dates
    ON PersonAssignments(PersonId, ValidFromLocalDate, ValidToLocalDate);
CREATE INDEX IF NOT EXISTS IX_PersonAssignments_Site_Dates
    ON PersonAssignments(SiteId, ValidFromLocalDate, ValidToLocalDate);

-- F. AttendanceStatusDefinitions
CREATE TABLE IF NOT EXISTS AttendanceStatusDefinitions (
    StatusId TEXT PRIMARY KEY,
    VisibleName TEXT NOT NULL,
    NormalizedName TEXT NOT NULL,
    ShortSymbol TEXT NOT NULL,
    ChosenColorHex TEXT NOT NULL,
    IsSystemSeeded INTEGER NOT NULL DEFAULT 0 CHECK (IsSystemSeeded IN (0, 1)),
    ActiveFlag INTEGER NOT NULL DEFAULT 1 CHECK (ActiveFlag IN (0, 1)),
    SortOrder INTEGER NOT NULL DEFAULT 100,
    CreatedAtUtc TEXT NOT NULL,
    RowVersion INTEGER NOT NULL DEFAULT 1
);

-- G. AttendanceStatusNameHistory
CREATE TABLE IF NOT EXISTS AttendanceStatusNameHistory (
    HistoryId INTEGER PRIMARY KEY AUTOINCREMENT,
    StatusId TEXT NOT NULL REFERENCES AttendanceStatusDefinitions(StatusId),
    VisibleNameSnapshot TEXT NOT NULL,
    ShortSymbolSnapshot TEXT NOT NULL,
    ColorSnapshot TEXT NOT NULL,
    ValidFromLocalDate TEXT NOT NULL,
    ValidToLocalDate TEXT NULL,
    RecordedAtUtc TEXT NOT NULL
);

-- H. AttendanceRecords (One primary committed record per PersonId + AttendanceLocalDate)
CREATE TABLE IF NOT EXISTS AttendanceRecords (
    RecordId INTEGER PRIMARY KEY AUTOINCREMENT,
    PersonId TEXT NOT NULL REFERENCES Persons(PersonId),
    AttendanceLocalDate TEXT NOT NULL,
    StatusId TEXT NOT NULL REFERENCES AttendanceStatusDefinitions(StatusId),
    StatusNameSnapshot TEXT NOT NULL,
    StatusSymbolSnapshot TEXT NOT NULL,
    StatusColorSnapshot TEXT NOT NULL,
    AssignmentIdAtEffectiveDate TEXT NOT NULL REFERENCES PersonAssignments(AssignmentId),
    SiteIdAtEffectiveDate TEXT NOT NULL REFERENCES Sites(SiteId),
    SiteNameSnapshot TEXT NOT NULL,
    Source TEXT NOT NULL, -- 'manual', 'bulk', '20h-auto', '20h-catchup', 'emergency-override'
    CreatedByUserId TEXT NOT NULL,
    CommittedAtUtc TEXT NOT NULL,
    RevisionNumber INTEGER NOT NULL DEFAULT 1,
    IdempotencyKey TEXT NOT NULL UNIQUE,
    UNIQUE(PersonId, AttendanceLocalDate)
);

CREATE INDEX IF NOT EXISTS IX_AttendanceRecords_Date_Site_Status
    ON AttendanceRecords(AttendanceLocalDate, SiteIdAtEffectiveDate, StatusId);
CREATE INDEX IF NOT EXISTS IX_AttendanceRecords_Person_Date
    ON AttendanceRecords(PersonId, AttendanceLocalDate);

-- I. AttendanceRevisions (Append-only revision log)
CREATE TABLE IF NOT EXISTS AttendanceRevisions (
    RevisionId INTEGER PRIMARY KEY AUTOINCREMENT,
    PersonId TEXT NOT NULL REFERENCES Persons(PersonId),
    TargetLocalDate TEXT NOT NULL,
    BeforeStatusId TEXT NOT NULL,
    BeforeStatusNameSnapshot TEXT NOT NULL,
    AfterStatusId TEXT NOT NULL,
    AfterStatusNameSnapshot TEXT NOT NULL,
    Reason TEXT NOT NULL,
    ActorUserId TEXT NOT NULL,
    ExecutedAtUtc TEXT NOT NULL,
    AuthorizationMethod TEXT NOT NULL
);

-- J. PersonnelMovements (Internal Transfer, External Transfer, Separation)
CREATE TABLE IF NOT EXISTS PersonnelMovements (
    MovementId TEXT PRIMARY KEY,
    PersonId TEXT NOT NULL REFERENCES Persons(PersonId),
    PersonNameSnapshot TEXT NOT NULL,
    ServiceNumberASnapshot TEXT NOT NULL,
    MovementType INTEGER NOT NULL, -- 1=InternalTransfer, 2=ExternalTransfer, 3=Separation
    SourceSiteId TEXT NOT NULL REFERENCES Sites(SiteId),
    SourceSiteNameSnapshot TEXT NOT NULL,
    DestinationSiteId TEXT NULL REFERENCES Sites(SiteId),
    DestinationSiteNameSnapshot TEXT NULL,
    DestinationExternalName TEXT NULL,
    SeparationReason TEXT NULL,
    EffectiveLocalDate TEXT NOT NULL,
    CommittedAtUtc TEXT NOT NULL,
    ActorUserId TEXT NOT NULL,
    IdempotencyKey TEXT NOT NULL UNIQUE
);

CREATE INDEX IF NOT EXISTS IX_PersonnelMovements_Person_Date
    ON PersonnelMovements(PersonId, EffectiveLocalDate);
CREATE INDEX IF NOT EXISTS IX_PersonnelMovements_Site_Date
    ON PersonnelMovements(SourceSiteId, DestinationSiteId, EffectiveLocalDate);

-- K. Users
CREATE TABLE IF NOT EXISTS Users (
    UserId TEXT PRIMARY KEY,
    LinkedPersonId TEXT NULL REFERENCES Persons(PersonId),
    Username TEXT NOT NULL UNIQUE,
    NormalizedUsername TEXT NOT NULL UNIQUE,
    Role INTEGER NOT NULL CHECK (Role IN (1, 2, 3)), -- 1=GeneralAdmin, 2=OrdinaryAdmin, 3=Supervisor
    CredentialHash TEXT NOT NULL,
    PasswordSalt TEXT NOT NULL,
    RequiresFirstRunSetup INTEGER NOT NULL DEFAULT 0 CHECK (RequiresFirstRunSetup IN (0, 1)),
    ActiveFlag INTEGER NOT NULL DEFAULT 1 CHECK (ActiveFlag IN (0, 1)),
    FailedLoginCount INTEGER NOT NULL DEFAULT 0,
    LockedUntilUtc TEXT NULL,
    CreatedByUserId TEXT NOT NULL,
    CreatedByDisplaySnapshot TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    RowVersion INTEGER NOT NULL DEFAULT 1
);

-- L. UserRoleHistory, UserSessions, FailedLoginEvents
CREATE TABLE IF NOT EXISTS UserRoleHistory (
    HistoryId INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId TEXT NOT NULL REFERENCES Users(UserId),
    OldRole INTEGER NULL,
    NewRole INTEGER NOT NULL,
    ChangedByUserId TEXT NOT NULL,
    ChangedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS UserSessions (
    SessionNonce TEXT PRIMARY KEY,
    UserId TEXT NOT NULL REFERENCES Users(UserId),
    Role INTEGER NOT NULL,
    IssuedAtUtc TEXT NOT NULL,
    ExpiresAtUtc TEXT NOT NULL,
    RevokedFlag INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS FailedLoginEvents (
    FailedEventId INTEGER PRIMARY KEY AUTOINCREMENT,
    UsernameAttempted TEXT NOT NULL,
    AttemptedAtUtc TEXT NOT NULL,
    FailureReason TEXT NOT NULL
);

-- M. AuditEvents (Immutable append-only evidence ledger with execution-time actor snapshots)
CREATE TABLE IF NOT EXISTS AuditEvents (
    AuditEventId TEXT PRIMARY KEY,
    OperationCode TEXT NOT NULL,
    OperationArabicName TEXT NOT NULL,
    ActorUserId TEXT NOT NULL,
    ActorPersonId TEXT NULL,
    ActorUsernameSnapshot TEXT NOT NULL,
    ActorRosterFullNameSnapshot TEXT NULL,
    TargetEntityType TEXT NOT NULL,
    TargetEntityId TEXT NOT NULL,
    TargetDisplaySnapshot TEXT NOT NULL,
    OldValueJson TEXT NULL,
    NewValueJson TEXT NULL,
    DefaultArabicDescription TEXT NOT NULL,
    Reason TEXT NULL,
    EffectiveLocalDate TEXT NOT NULL,
    ExecutedAtUtc TEXT NOT NULL,
    CorrelationId TEXT NOT NULL,
    EventHash TEXT NOT NULL,
    PriorEventHash TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_AuditEvents_ExecutedAtUtc ON AuditEvents(ExecutedAtUtc DESC);
CREATE INDEX IF NOT EXISTS IX_AuditEvents_Actor ON AuditEvents(ActorUserId, ActorUsernameSnapshot, ActorRosterFullNameSnapshot);
CREATE INDEX IF NOT EXISTS IX_AuditEvents_Target ON AuditEvents(TargetEntityId, TargetDisplaySnapshot);

-- N. AuditLogPresentationEdits (Mutable UI-facing overlay authored ONLY by General/Ordinary Admins)
CREATE TABLE IF NOT EXISTS AuditLogPresentationEdits (
    OverlayId INTEGER PRIMARY KEY AUTOINCREMENT,
    AuditEventId TEXT NOT NULL REFERENCES AuditEvents(AuditEventId),
    OverlayVersion INTEGER NOT NULL,
    IsTombstoned INTEGER NOT NULL DEFAULT 0 CHECK (IsTombstoned IN (0, 1)),
    CorrectedDescription TEXT NULL,
    Reason TEXT NOT NULL,
    EditedByUserId TEXT NOT NULL,
    EditedByDisplaySnapshot TEXT NOT NULL,
    EditedAtUtc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_AuditLogPresentationEdits_Event
    ON AuditLogPresentationEdits(AuditEventId, OverlayVersion DESC);

-- O. CustomFieldDefinitions and CustomFieldValues
CREATE TABLE IF NOT EXISTS CustomFieldDefinitions (
    FieldDefinitionId TEXT PRIMARY KEY,
    TechnicalKey TEXT NOT NULL UNIQUE,
    ArabicCaption TEXT NOT NULL,
    FieldType INTEGER NOT NULL DEFAULT 1,
    DisplayOrder INTEGER NOT NULL,
    IsSystemProtected INTEGER NOT NULL DEFAULT 0 CHECK (IsSystemProtected IN (0, 1)),
    VisibleFlag INTEGER NOT NULL DEFAULT 1 CHECK (VisibleFlag IN (0, 1)),
    ActiveFlag INTEGER NOT NULL DEFAULT 1 CHECK (ActiveFlag IN (0, 1)),
    CreatedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS CustomFieldValues (
    PersonId TEXT NOT NULL REFERENCES Persons(PersonId),
    FieldDefinitionId TEXT NOT NULL REFERENCES CustomFieldDefinitions(FieldDefinitionId),
    FieldValueText TEXT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (PersonId, FieldDefinitionId)
);

-- P. PeriodClosures
CREATE TABLE IF NOT EXISTS PeriodClosures (
    PeriodKey TEXT PRIMARY KEY, -- YYYY-MM-DD or YYYY-MM
    PeriodType TEXT NOT NULL CHECK (PeriodType IN ('DAY', 'MONTH')),
    IsClosed INTEGER NOT NULL DEFAULT 1,
    ClosedAtUtc TEXT NOT NULL,
    ClosedByActor TEXT NOT NULL,
    PolicyVersion TEXT NOT NULL
);

-- Q. AttendanceDrafts
CREATE TABLE IF NOT EXISTS AttendanceDrafts (
    DraftId TEXT PRIMARY KEY,
    BusinessDate TEXT NOT NULL,
    SiteId TEXT NULL,
    TargetStatusId TEXT NOT NULL,
    SelectedPersonIdsJson TEXT NOT NULL,
    CreatedByUserId TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

-- R. AutoAttendanceRuns (20:00 carry-forward & startup catch-up ledger)
CREATE TABLE IF NOT EXISTS AutoAttendanceRuns (
    JobKey TEXT PRIMARY KEY, -- e.g. AUTO20H-2026-10-16
    BusinessDate TEXT NOT NULL UNIQUE,
    DueTimeLocal TEXT NOT NULL,
    StartedAtUtc TEXT NOT NULL,
    CompletedAtUtc TEXT NOT NULL,
    IsCatchUpRun INTEGER NOT NULL DEFAULT 0 CHECK (IsCatchUpRun IN (0, 1)),
    CopiedCount INTEGER NOT NULL DEFAULT 0,
    SkippedNoYesterdayCount INTEGER NOT NULL DEFAULT 0,
    SkippedTransferExemptCount INTEGER NOT NULL DEFAULT 0,
    SummaryArabic TEXT NOT NULL
);

-- S. BackupHistory
CREATE TABLE IF NOT EXISTS BackupHistory (
    BackupId TEXT PRIMARY KEY,
    BackupFilePath TEXT NOT NULL,
    Sha256Checksum TEXT NOT NULL,
    SchemaVersion INTEGER NOT NULL,
    IsAutomatic INTEGER NOT NULL DEFAULT 0,
    VerificationPassed INTEGER NOT NULL DEFAULT 1,
    KeyProtectionMode TEXT NOT NULL,
    CreatedByUserId TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    NotesArabic TEXT NOT NULL
);

-- T. ImportBatches and ImportIssues
CREATE TABLE IF NOT EXISTS ImportBatches (
    ImportBatchId TEXT PRIMARY KEY,
    SourceFileName TEXT NOT NULL,
    SourceFileSha256 TEXT NOT NULL,
    TotalRows INTEGER NOT NULL,
    AcceptedRows INTEGER NOT NULL,
    RejectedRows INTEGER NOT NULL,
    CreatedSitesCount INTEGER NOT NULL,
    Status TEXT NOT NULL, -- 'STAGED', 'COMMITTED', 'CANCELLED'
    CreatedByUserId TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    CommittedAtUtc TEXT NULL
);

CREATE TABLE IF NOT EXISTS ImportIssues (
    IssueId INTEGER PRIMARY KEY AUTOINCREMENT,
    ImportBatchId TEXT NOT NULL REFERENCES ImportBatches(ImportBatchId),
    WorksheetName TEXT NOT NULL,
    RowNumber INTEGER NOT NULL,
    RawServiceNumberA TEXT NULL,
    RawPersonName TEXT NULL,
    RawSiteName TEXT NULL,
    IssueReasonArabic TEXT NOT NULL
);

-- AppSettings (Local configuration: theme, backup time 16:00, preferred E:\ path, clock watermark)
CREATE TABLE IF NOT EXISTS AppSettings (
    SettingKey TEXT PRIMARY KEY,
    SettingValue TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

-- =============================================================================
-- SEED DATA (Protected System Site "غير محدد", 4 Default Statuses, System Columns, system defaults)
-- =============================================================================
INSERT OR IGNORE INTO SchemaVersions (SchemaVersion, MigrationName, AppliedAtUtc)
VALUES (1, 'V001_InitialSchema', STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'));

INSERT OR IGNORE INTO Sites (SiteId, VisibleName, NormalizedName, IsSystemSite, ActiveFlag, CreatedAtUtc, RowVersion)
VALUES ('SITE-UNSPECIFIED', 'غير محدد', 'غير محدد', 1, 1, STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'), 1);

INSERT OR IGNORE INTO SiteNameHistory (SiteId, VisibleNameSnapshot, ValidFromLocalDate, ValidToLocalDate, ChangedByUserId, RecordedAtUtc)
VALUES ('SITE-UNSPECIFIED', 'غير محدد', '2020-01-01', NULL, 'USR-SYSTEM', STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'));

INSERT OR IGNORE INTO AttendanceStatusDefinitions (StatusId, VisibleName, NormalizedName, ShortSymbol, ChosenColorHex, IsSystemSeeded, ActiveFlag, SortOrder, CreatedAtUtc, RowVersion)
VALUES
    ('STS-PRESENT',    'موجود', 'موجود', 'م', '#38BDF8', 1, 1, 1, STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'), 1),
    ('STS-LEAVE',      'إجازة', 'اجازه', 'ج', '#22C55E', 1, 1, 2, STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'), 1),
    ('STS-ABSENT',     'غياب',  'غياب',  'غ', '#EF4444', 1, 1, 3, STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'), 1),
    ('STS-PERMISSION', 'إذن',   'اذن',   'إ', '#EAB308', 1, 1, 4, STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'), 1);

INSERT OR IGNORE INTO AttendanceStatusNameHistory (StatusId, VisibleNameSnapshot, ShortSymbolSnapshot, ColorSnapshot, ValidFromLocalDate, ValidToLocalDate, RecordedAtUtc)
VALUES
    ('STS-PRESENT',    'موجود', 'م', '#38BDF8', '2020-01-01', NULL, STRFTIME('%Y-%m-%dT%H:%M:%fZ','now')),
    ('STS-LEAVE',      'إجازة', 'ج', '#22C55E', '2020-01-01', NULL, STRFTIME('%Y-%m-%dT%H:%M:%fZ','now')),
    ('STS-ABSENT',     'غياب',  'غ', '#EF4444', '2020-01-01', NULL, STRFTIME('%Y-%m-%dT%H:%M:%fZ','now')),
    ('STS-PERMISSION', 'إذن',   'إ', '#EAB308', '2020-01-01', NULL, STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'));

INSERT OR IGNORE INTO CustomFieldDefinitions (FieldDefinitionId, TechnicalKey, ArabicCaption, FieldType, DisplayOrder, IsSystemProtected, VisibleFlag, ActiveFlag, CreatedAtUtc)
VALUES
    ('FLD-SYS-ROWNUM',  'sys_row_number',       'الرقم',     2, 1, 1, 1, 1, STRFTIME('%Y-%m-%dT%H:%M:%fZ','now')),
    ('FLD-SYS-SVC-NUM', 'sys_service_number_a', 'الرقم (ع)', 1, 2, 1, 1, 1, STRFTIME('%Y-%m-%dT%H:%M:%fZ','now')),
    ('FLD-SYS-NAME',    'sys_person_name',      'الاسم',     1, 3, 1, 1, 1, STRFTIME('%Y-%m-%dT%H:%M:%fZ','now')),
    ('FLD-SYS-SITE',    'sys_site_name',        'الموقع',    1, 4, 1, 1, 1, STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'));

INSERT OR IGNORE INTO AppSettings (SettingKey, SettingValue, UpdatedAtUtc)
VALUES
    ('Theme', 'light', STRFTIME('%Y-%m-%dT%H:%M:%fZ','now')),
    ('AutoBackupTimeLocal', '16:00', STRFTIME('%Y-%m-%dT%H:%M:%fZ','now')),
    ('PreferredBackupDirectory', 'E:\AlTamamAlYawmi\Backups', STRFTIME('%Y-%m-%dT%H:%M:%fZ','now')),
    ('BackupRetentionCount', '14', STRFTIME('%Y-%m-%dT%H:%M:%fZ','now')),
    ('AllowClosedDayEmergencyOverride', 'false', STRFTIME('%Y-%m-%dT%H:%M:%fZ','now')),
    ('LastObservedUtcTimestamp', STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'), STRFTIME('%Y-%m-%dT%H:%M:%fZ','now'));

-- Disallow invalid or overlapping effective-dated site membership at the storage boundary.
CREATE TRIGGER IF NOT EXISTS TR_PersonAssignments_ValidateInsert
BEFORE INSERT ON PersonAssignments
BEGIN
    SELECT RAISE(ABORT, 'Invalid assignment date interval')
      WHERE NEW.ValidToLocalDate IS NOT NULL AND NEW.ValidToLocalDate <= NEW.ValidFromLocalDate;
    SELECT RAISE(ABORT, 'Overlapping person assignments')
      WHERE EXISTS (
          SELECT 1 FROM PersonAssignments p
          WHERE p.PersonId = NEW.PersonId
            AND NEW.ValidFromLocalDate < COALESCE(p.ValidToLocalDate, '9999-12-31')
            AND p.ValidFromLocalDate < COALESCE(NEW.ValidToLocalDate, '9999-12-31')
      );
END;
CREATE TRIGGER IF NOT EXISTS TR_PersonAssignments_ValidateUpdate
BEFORE UPDATE OF PersonId, ValidFromLocalDate, ValidToLocalDate ON PersonAssignments
BEGIN
    SELECT RAISE(ABORT, 'Invalid assignment date interval')
      WHERE NEW.ValidToLocalDate IS NOT NULL AND NEW.ValidToLocalDate <= NEW.ValidFromLocalDate;
    SELECT RAISE(ABORT, 'Overlapping person assignments')
      WHERE EXISTS (
          SELECT 1 FROM PersonAssignments p
          WHERE p.PersonId = NEW.PersonId AND p.AssignmentId <> OLD.AssignmentId
            AND NEW.ValidFromLocalDate < COALESCE(p.ValidToLocalDate, '9999-12-31')
            AND p.ValidFromLocalDate < COALESCE(NEW.ValidToLocalDate, '9999-12-31')
      );
END;
