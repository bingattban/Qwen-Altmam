using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace AlTamam.Tests.Bridge;

// Every destructive operation is confined to a unique disposable database root.
// Full reset is an explicitly confirmed product command, never maintenance cleanup.
[Collection("SQLCipherGlobalPool")]
public sealed class R01NativeRecoveryLifecycleTests : IAsyncLifetime, IDisposable
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _utc;
        public Clock() { var now = System.GetLocalNow(); _utc = new DateTimeOffset(now.Date.AddHours(12), now.Offset).ToUniversalTime(); }
        public override TimeZoneInfo LocalTimeZone => System.LocalTimeZone;
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(int seconds) => _utc = _utc.AddSeconds(seconds);
    }
    private readonly Clock _clock = new();
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "AlTamam-R01-Recovery-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private AlTamamDbContext _db = null!;
    private BridgeSharedServices _services = null!;
    private WebView2BridgeDispatcher _dispatcher = null!;
    private string _nonce = new('0', 32);
    private const string P = "R01-REC-P1", Archived = "R01-REC-ARCH", ReturnSite = "R01-REC-RETURN";
    private string Day(int offset = 0) => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime).AddDays(offset).ToString("yyyy-MM-dd");
    public R01NativeRecoveryLifecycleTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex, useConnectionPooling: false);
        await _db.InitializeAsync();
        _services = new BridgeSharedServices(_db, _clock); _dispatcher = BridgeHandlerCatalog.Create(_services);
        var login = await Object("AuthenticateLocalUser", new JsonObject {
            ["username"] = "r01-recovery-owner", ["password"] = "Native123",
            ["passwordConfirmation"] = "Native123", ["setupIntent"] = "firstTimeSetup"
        });
        _nonce = login["sessionNonce"]!.GetValue<string>();
        Assert.NotNull(await _services.ResolveSessionAsync(_nonce));
        await Object("SetOutputDirectory", new JsonObject { ["directory"] = _folder });
        await Execute("""
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,NormalizedServiceNumberA,
                FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('R01-REC-P1','فرد قائم اختباري','فرد قائم اختباري','760001','760001',$start,'2026-01-01T00:00:00Z',1,1,1);
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,ValidFromLocalDate,
                ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('R01-REC-P1','فرد قائم اختباري','760001',$start,'USR-SYSTEM','2026-01-01T00:00:00Z','synthetic history');
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,CreatedAtUtc)
            VALUES('R01-REC-A1','R01-REC-P1','SITE-UNSPECIFIED',$start,'2026-01-01T00:00:00Z');
            INSERT INTO Sites(SiteId,VisibleName,NormalizedName,IsSystemSite,ActiveFlag,CreatedAtUtc,RowVersion)
            VALUES('R01-REC-RETURN','موقع العودة الاختباري','موقع العوده الاختباري',0,1,'2026-01-01T00:00:00Z',1);
            """, ("$start", Day(-10)));
    }
    private async Task Execute(string sql, params (string key, object value)[] parameters)
    {
        using var connection = _db.CreateOpenConnection(); using var command = connection.CreateCommand();
        command.CommandText = sql; foreach (var p in parameters) command.Parameters.AddWithValue(p.key, p.value);
        await command.ExecuteNonQueryAsync();
    }
    private string Scalar(string sql)
    {
        using var connection = _db.CreateOpenConnection(); using var command = connection.CreateCommand();
        command.CommandText = sql; return Convert.ToString(command.ExecuteScalar()) ?? "";
    }
    private long Audits() => long.Parse(Scalar("SELECT COUNT(*) FROM AuditEvents;"));
    private async Task<BridgeResponse> Dispatch(string action, JsonObject? payload = null)
    {
        payload ??= new JsonObject();
        if (ActionManifest.Find(action)!.IsMutating && !payload.ContainsKey("operationId")) payload["operationId"] = Guid.NewGuid().ToString("N");
        var result = await _dispatcher.DispatchAsync(new JsonObject {
            ["requestId"] = Guid.NewGuid().ToString("N"), ["protocolVersion"] = BridgeProtocol.ProtocolVersion,
            ["action"] = action, ["sessionNonce"] = _nonce, ["payload"] = payload.DeepClone()
        }.ToJsonString(), LocalWebUiOrigin.BaseUrl, true, nonce => _services.ResolveSessionAsync(nonce));
        R01ResponseSchema.AssertMatches(result.Response, action); return result.Response;
    }
    private BridgeResponse Record(string action, BridgeResponse response)
    {
        Assert.True(response.Success, action + ": " + response.ErrorCode + " " + response.UserMessage);
        _output.WriteLine("R01_NATIVE_SHAPE " + action + " " + R01WireShapeSnapshot.Shape(response)); return response;
    }
    private async Task<BridgeResponse> Call(string action, JsonObject? payload = null) => Record(action, await Dispatch(action, payload));
    private async Task<JsonObject> Object(string action, JsonObject? payload = null) => Assert.IsType<JsonObject>((await Call(action, payload)).Data);
    private async Task Replay(string action, JsonObject intent, JsonObject original)
    {
        long count = Audits(); Assert.True(JsonNode.DeepEquals(original, await Object(action, intent))); Assert.Equal(count, Audits());
    }

    [Fact]
    public async Task ArchivedReturn_RealPreviewCommit_ReusesIdentityAndRetainsExitHistory()
    {
        await Execute("""
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,NormalizedServiceNumberA,
                FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('R01-REC-ARCH','فرد مؤرشف اختباري','فرد مورشف اختباري','004141','004141',$start,'2026-01-01T00:00:00Z',2,0,2);
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,ValidFromLocalDate,
                ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('R01-REC-ARCH','فرد مؤرشف اختباري','004141',$start,'USR-SYSTEM','2026-01-01T00:00:00Z','original identity');
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,ValidToLocalDate,CreatedAtUtc)
            VALUES('R01-REC-ARCH-A','R01-REC-ARCH','SITE-UNSPECIFIED',$start,$exit,'2026-01-01T00:00:00Z');
            INSERT INTO PersonnelMovements(MovementId,PersonId,PersonNameSnapshot,ServiceNumberASnapshot,MovementType,
                SourceSiteId,SourceSiteNameSnapshot,DestinationExternalName,EffectiveLocalDate,CommittedAtUtc,ActorUserId,IdempotencyKey)
            VALUES('R01-REC-EXIT','R01-REC-ARCH','فرد مؤرشف اختباري','004141',2,'SITE-UNSPECIFIED','غير محدد',
                'جهة خروج اختبارية',$exit,'2026-01-01T00:00:00Z','USR-SYSTEM','R01-REC-EXIT');
            """, ("$start", Day(-10)), ("$exit", Day(-1)));
        long before = Audits();
        var preview = await Object("PreviewArchivedIdentity", new JsonObject {
            ["visibleName"] = "فرد عائد اختباري", ["serviceNumberA"] = "004141", ["siteId"] = ReturnSite
        });
        Assert.True(preview["requiresResolution"]!.GetValue<bool>());
        Assert.Equal(Archived, Assert.Single(Assert.IsType<JsonArray>(preview["candidates"]))!["personId"]!.GetValue<string>());
        Assert.Equal(before, Audits());
        var intent = new JsonObject { ["previewToken"] = preview["previewToken"]!.DeepClone(), ["personId"] = Archived, ["decision"] = "same" };
        var returned = await Object("CommitArchivedReturn", intent); await Replay("CommitArchivedReturn", intent, returned);
        Assert.Equal(Archived, returned["personId"]!.GetValue<string>()); Assert.True(returned["returned"]!.GetValue<bool>());
        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM Persons;"));
        Assert.Equal("1", Scalar("SELECT ActiveFlag FROM Persons WHERE PersonId='R01-REC-ARCH';"));
        Assert.Equal(Day(-1), Scalar("SELECT ValidToLocalDate FROM PersonAssignments WHERE AssignmentId='R01-REC-ARCH-A';"));
        Assert.Equal(ReturnSite, Scalar("SELECT SiteId FROM PersonAssignments WHERE PersonId='R01-REC-ARCH' AND ValidToLocalDate IS NULL;"));
        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM PersonIdentityHistory WHERE PersonId='R01-REC-ARCH';"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM PersonnelMovements WHERE MovementType=4;"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM PersonnelMovements WHERE MovementId='R01-REC-EXIT';"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='PERSON_RETURN';"));
        Assert.True(_db.HasValidIntegrity());
    }

    [Fact]
    public async Task CatchUp_RealPreviewCommit_FillsOnlyMissingDayWithOneLedgerAndDurableReceipt()
    {
        await Object("SetAutoAttendanceDefaultStatus", new JsonObject { ["statusId"] = "STS-PRESENT" });
        var preview = await Object("PreviewAdministrativeAttendanceCatchUp", new JsonObject {
            ["businessDate"] = Day(-2), ["reason"] = "استدراك اختباري موثق ليوم ناقص"
        });
        Assert.Equal(1, preview["pendingCount"]!.GetValue<int>());
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM AttendanceRecords;"));
        var intent = new JsonObject { ["previewToken"] = preview["previewToken"]!.DeepClone() };
        var saved = await Object("CommitAdministrativeAttendanceCatchUp", intent); await Replay("CommitAdministrativeAttendanceCatchUp", intent, saved);
        Assert.Equal(1, saved["appliedCount"]!.GetValue<int>());
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM AttendanceRecords;"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM AutoAttendanceRuns;"));
        Assert.Equal("08h-admin-catchup", Scalar("SELECT Source FROM AttendanceRecords;"));
        Assert.Equal("STS-PRESENT", Scalar("SELECT StatusId FROM AttendanceRecords;"));
        var stale = (JsonObject)intent.DeepClone(); stale["operationId"] = Guid.NewGuid().ToString("N");
        long before = Audits(); Assert.False((await Dispatch("CommitAdministrativeAttendanceCatchUp", stale)).Success); Assert.Equal(before, Audits());
        Assert.True(_db.HasValidIntegrity());
    }

    [Fact]
    public async Task ClosedCorrection_RealPolicyPreviewCommit_KeepsClosureOriginalTimeAndRevisionEvidence()
    {
        string originalTime = Day(-2) + "T09:30:00.0000000Z";
        await Execute("""
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,StatusNameSnapshot,StatusSymbolSnapshot,
                StatusColorSnapshot,AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,Source,
                CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
            VALUES('R01-REC-P1',$day,'STS-PRESENT','موجود','م','#22C55E','R01-REC-A1','SITE-UNSPECIFIED',
                'غير محدد','manual','R01-OLD-ACTOR',$time,1,'R01-REC-OLD-ATT');
            INSERT INTO PeriodClosures(PeriodKey,PeriodType,IsClosed,ClosedAtUtc,ClosedByActor,PolicyVersion)
            VALUES($day,'DAY',1,$time,'R01-OLD-ACTOR','v1');
            """, ("$day", Day(-2)), ("$time", originalTime));
        var request = new JsonObject { ["personId"] = P, ["businessDate"] = Day(-2), ["statusId"] = "STS-ABSENT", ["reason"] = "تصحيح اختباري موثق بالمستندات" };
        Assert.False((await Dispatch("PreviewClosedDayCorrection", request)).Success);
        var enable = new JsonObject { ["enabled"] = true, ["reason"] = "تمكين اختباري مؤقت للتصحيح" };
        var enabled = await Object("SetClosedDayCorrectionPolicy", enable); await Replay("SetClosedDayCorrectionPolicy", enable, enabled);
        var preview = await Object("PreviewClosedDayCorrection", request);
        Assert.Equal("موجود", preview["beforeStatusName"]!.GetValue<string>()); Assert.Equal("غياب", preview["afterStatusName"]!.GetValue<string>());
        var intent = new JsonObject { ["previewToken"] = preview["previewToken"]!.DeepClone() };
        var corrected = await Object("CommitClosedDayCorrection", intent); await Replay("CommitClosedDayCorrection", intent, corrected);
        Assert.Equal("STS-ABSENT", Scalar("SELECT StatusId FROM AttendanceRecords;"));
        Assert.Equal(originalTime, Scalar("SELECT CommittedAtUtc FROM AttendanceRecords;"));
        Assert.Equal("R01-OLD-ACTOR", Scalar("SELECT CreatedByUserId FROM AttendanceRecords;"));
        Assert.Equal("1", Scalar("SELECT IsClosed FROM PeriodClosures;"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM AttendanceRevisions;"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='CLOSED_DAY_CORRECTED';"));
        await Object("SetClosedDayCorrectionPolicy", new JsonObject { ["enabled"] = false, ["reason"] = "إنهاء التمكين الاختباري المؤقت" });
        Assert.False((await Dispatch("PreviewClosedDayCorrection", request)).Success);
        Assert.True(_db.HasValidIntegrity());
    }

    [Fact]
    public async Task Backup_RealCreateVerifyPickerAndRestorePreview_PreserveLiveDataAndEncryptedCandidate()
    {
        var backup = await Object("CreateVerifiedBackup"); string path = backup["path"]!.GetValue<string>();
        string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();
        Assert.Equal(hash, backup["sha256"]!.GetValue<string>());
        Assert.False((await File.ReadAllBytesAsync(path)).AsSpan(0, 16).SequenceEqual(Encoding.ASCII.GetBytes("SQLite format 3\0")));
        using (var copy = new AlTamamDbContext(path, SyntheticFixtures.TestKeyHex, useConnectionPooling: false)) Assert.True(copy.HasValidIntegrity());
        var verified = await Object("VerifyBackup", new JsonObject { ["path"] = path });
        Assert.True(verified["verified"]!.GetValue<bool>()); Assert.True(verified["registeredInCurrentCatalog"]!.GetValue<bool>());
        Assert.Equal(1L, verified["people"]!.GetValue<long>());
        // Only the OS selection boundary is supplied; all native inspection/SQL is real.
        _services.PickBackupFileAsync = folder => { Assert.Equal(_folder, folder); return Task.FromResult<string?>(path); };
        var picked = await Object("InspectBackup"); Assert.Equal(hash, picked["sha256"]!.GetValue<string>());
        await Object("CreatePerson", new JsonObject { ["visibleName"] = "فرد بعد النسخة الاختبارية", ["serviceNumberA"] = "760003" });
        var preview = await Object("PreviewRestore", new JsonObject { ["path"] = path, ["expectedSha256"] = hash });
        Assert.True(preview["previewOnly"]!.GetValue<bool>()); Assert.False(preview["restoreEnabled"]!.GetValue<bool>());
        Assert.Equal(1L, preview["incoming"]!["people"]!.GetValue<long>()); Assert.Equal(2L, preview["current"]!["people"]!.GetValue<long>());
        Assert.False((await Dispatch("PreviewRestore", new JsonObject { ["path"] = path, ["expectedSha256"] = new string('0', 64) })).Success);
        Assert.False((await Dispatch("RestoreDatabaseAtomically", new JsonObject { ["path"] = path, ["expectedSha256"] = hash })).Success);
        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM Persons;"));
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant());
        Assert.True(_db.HasValidIntegrity());
    }

    [Fact]
    public async Task FullReset_RealConfirmedCommand_ReturnsTruthfulSuccessAfterEndingItsOwnSession()
    {
        var preview = await Object("PreviewFullReset");
        var intent = new JsonObject {
            ["previewToken"] = preview["previewToken"]!.DeepClone(), ["expectedRevision"] = preview["expectedRevision"]!.DeepClone(),
            ["password"] = "Native123", ["confirmation"] = "تصفير جميع البيانات",
            ["confirmDeleteLiveData"] = true, ["confirmDeleteOwnedBackups"] = true
        };
        Assert.False((await Dispatch("CommitFullReset", intent)).Success); // Real server countdown.
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM Persons;"));
        _clock.Advance(6);
        var wire = await Dispatch("CommitFullReset", intent);
        // Prove whether reset COMMITTED before asserting its wire result.
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM Persons;"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM Users;"));
        Assert.Null(await _services.ResolveSessionAsync(_nonce));
        string rescue = Assert.Single(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(_path)!, "backups"), "rescue-before-reset-*.dbbackup"));
        using (var rescued = new AlTamamDbContext(rescue, SyntheticFixtures.TestKeyHex, useConnectionPooling: false))
        {
            Assert.True(rescued.HasValidIntegrity()); using var c = rescued.CreateOpenConnection(); using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM Persons;"; Assert.Equal(1L, Convert.ToInt64(q.ExecuteScalar()));
            q.CommandText = "SELECT COUNT(*) FROM Users;"; Assert.Equal(1L, Convert.ToInt64(q.ExecuteScalar()));
        }
        Record("CommitFullReset", wire);
        var data = Assert.IsType<JsonObject>(wire.Data);
        Assert.True(data["reset"]!.GetValue<bool>()); Assert.True(data["requiresFirstSetup"]!.GetValue<bool>());
        Assert.Equal(rescue, data["rescuePath"]!.GetValue<string>());
        Assert.False((await Object("GetProvisioningState"))["provisioned"]!.GetValue<bool>());
        Assert.Equal(BridgeErrorCodes.SessionExpired, (await Dispatch("GetCurrentSession")).ErrorCode);
        Assert.True(_db.HasValidIntegrity());
    }

    [Fact]
    public async Task CancelExport_RealRunningNativeWriter_StopsWithoutPublishingOrChangingSqlCipher()
    {
        await Execute("""
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i+1 FROM n WHERE i<9999)
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,NormalizedServiceNumberA,
                FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
            SELECT 'R01-CANCEL-'||i,'فرد الغاء '||i,'فرد الغاء '||i,'887'||printf('%05d',i),'887'||printf('%05d',i),
                $day,'2026-01-01T00:00:00Z',1,1,1 FROM n;
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,CreatedAtUtc)
            SELECT 'R01-CANCEL-A-'||PersonId,PersonId,'SITE-UNSPECIFIED',$day,'2026-01-01T00:00:00Z'
            FROM Persons WHERE PersonId LIKE 'R01-CANCEL-%';
            """, ("$day", Day(-1)));
        long before = Audits();
        var payload = new JsonObject { ["year"] = DateOnly.Parse(Day()).Year, ["month"] = DateOnly.Parse(Day()).Month,
            ["term"] = "الغاء", ["consolidatedOnly"] = true };
        var exporting = Task.Run(() => Dispatch("ExportMonthlyWorkbookSet", payload));
        try
        {
            var watch = Stopwatch.StartNew(); BridgeResponse? running = null;
            while (watch.Elapsed < TimeSpan.FromSeconds(30) && !exporting.IsCompleted)
            {
                var progress = await Dispatch("GetExportProgress");
                if (progress.Success && progress.Data is JsonObject d && d["total"]?.ToString() == "10000" &&
                    d["completed"]?.GetValue<bool>() == false) { running = progress; break; }
                await Task.Delay(10);
            }
            Assert.NotNull(running); Record("GetExportProgress", running!);
            var canceled = await Object("CancelExport"); Assert.True(canceled["cancelRequested"]!.GetValue<bool>());
            var result = await exporting;
            Assert.False(result.Success); Assert.Equal(BridgeErrorCodes.StorageBusy, result.ErrorCode);
            var final = await Object("GetExportProgress");
            Assert.True(final["cancelRequested"]!.GetValue<bool>()); Assert.False(final["completed"]!.GetValue<bool>());
            Assert.Equal("canceled", final["status"]!.GetValue<string>());
            Assert.Empty(Directory.GetFiles(_folder, "*.xlsx", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(_folder, "*.part", SearchOption.AllDirectories));
            Assert.Equal("10001", Scalar("SELECT COUNT(*) FROM Persons;")); Assert.Equal(before, Audits());
            Assert.True(_db.HasValidIntegrity());
        }
        finally
        {
            if (!exporting.IsCompleted) { await Dispatch("CancelExport"); await exporting; }
        }
    }
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() { _db?.Dispose(); SyntheticFixtures.DeleteTempDatabase(_path); if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
