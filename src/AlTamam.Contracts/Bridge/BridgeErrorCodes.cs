namespace AlTamam.Contracts.Bridge;

/// <summary>
/// All 24 minimum canonical error codes required by the C# <-> WebView2 UI Bridge contract.
/// </summary>
public static class BridgeErrorCodes
{
    public const string Unauthorized = "Unauthorized";
    public const string SessionExpired = "SessionExpired";
    public const string MissingData = "MissingData";
    public const string ValidationError = "ValidationError";
    public const string DuplicateServiceNumber = "DuplicateServiceNumber";
    public const string DuplicateUsername = "DuplicateUsername";
    public const string ConflictStale = "ConflictStale";
    public const string AlreadyPrepared = "AlreadyPrepared";
    public const string TransferDayAttendanceForbidden = "TransferDayAttendanceForbidden";
    public const string InvalidTransferSites = "InvalidTransferSites";
    public const string OverlappingAssignments = "OverlappingAssignments";
    public const string HistoricDayLocked = "HistoricDayLocked";
    public const string CustomStatusInactive = "CustomStatusInactive";
    public const string ImportPlanStale = "ImportPlanStale";
    public const string InvalidWorkbook = "InvalidWorkbook";
    public const string ExcelExportFailed = "ExcelExportFailed";
    public const string BackupIntegrityFailed = "BackupIntegrityFailed";
    public const string WrongRecoveryKey = "WrongRecoveryKey";
    public const string MissingEVolume = "MissingEVolume";
    public const string InsufficientDiskSpace = "InsufficientDiskSpace";
    public const string ScheduleUnavailable = "ScheduleUnavailable";
    public const string ResetBackupGateFailed = "ResetBackupGateFailed";
    public const string StorageBusy = "StorageBusy";
    public const string ClockRollbackSuspected = "ClockRollbackSuspected";

    public static readonly string[] AllCanonicalErrorCodes = new[]
    {
        Unauthorized, SessionExpired, MissingData, ValidationError,
        DuplicateServiceNumber, DuplicateUsername, ConflictStale, AlreadyPrepared,
        TransferDayAttendanceForbidden, InvalidTransferSites, OverlappingAssignments,
        HistoricDayLocked, CustomStatusInactive, ImportPlanStale, InvalidWorkbook,
        ExcelExportFailed, BackupIntegrityFailed, WrongRecoveryKey, MissingEVolume,
        InsufficientDiskSpace, ScheduleUnavailable, ResetBackupGateFailed,
        StorageBusy, ClockRollbackSuspected
    };
}
