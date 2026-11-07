using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace AlTamam.Tests.Bridge;

// All commands use a real provisioned session and the production dispatcher.
// Direct SQL below seeds independent prior-day history or checks durable facts.
public sealed class R01NativeOperationalLifecycleTests : IAsyncLifetime, IDisposable
{
    private sealed class BusinessClock : TimeProvider
    {
        private DateTimeOffset _utc;
        public BusinessClock()
        {
            var local = TimeProvider.System.GetLocalNow();
            _utc = new DateTimeOffset(local.Date.AddHours(12), local.Offset).ToUniversalTime();
        }
        public override TimeZoneInfo LocalTimeZone => TimeProvider.System.LocalTimeZone;
        public override DateTimeOffset GetUtcNow() => _utc;
        public void AdvanceSeconds(int seconds) => _utc = _utc.AddSeconds(seconds);
    }

    private readonly BusinessClock _clock = new();
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private readonly ITestOutputHelper _output;
    private AlTamamDbContext _db = null!;
    private BridgeSharedServices _services = null!;
    private WebView2BridgeDispatcher _dispatcher = null!;
    private string _nonce = new('0', 32);
    private string Today => _clock.GetLocalNow().ToString("yyyy-MM-dd");
    private const string P1 = "R01-OPS-P1", P2 = "R01-OPS-P2", P3 = "R01-OPS-P3";
    private const string Source = "SITE-UNSPECIFIED", Destination = "R01-OPS-DEST";
    public R01NativeOperationalLifecycleTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex, useConnectionPooling: false);
        await _db.InitializeAsync();
        _services = new BridgeSharedServices(_db, _clock);
        _dispatcher = BridgeHandlerCatalog.Create(_services);
        var login = await Object("AuthenticateLocalUser", new JsonObject {
            ["username"] = "r01-operations-owner", ["password"] = "Native123",
            ["passwordConfirmation"] = "Native123", ["setupIntent"] = "firstTimeSetup"
        });
        _nonce = login["sessionNonce"]!.GetValue<string>();
        Assert.NotNull(await _services.ResolveSessionAsync(_nonce));
        using var connection = _db.CreateOpenConnection();
        using (var site = connection.CreateCommand())
        {
            site.CommandText = """
                INSERT INTO Sites(SiteId,VisibleName,NormalizedName,IsSystemSite,ActiveFlag,CreatedAtUtc,RowVersion)
                VALUES('R01-OPS-DEST','موقع وجهة اختباري','موقع وجهه اختباري',0,1,'2026-01-01T00:00:00Z',1);
                """;
            await site.ExecuteNonQueryAsync();
        }
        for (int i = 1; i <= 3; i++)
        {
            using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
                    NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
                    OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES($id,$name,$name,$number,$number,$start,'2026-01-01T00:00:00Z',1,1,1);
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,CreatedAtUtc)
                VALUES($assignment,$id,'SITE-UNSPECIFIED',$start,'2026-01-01T00:00:00Z');
                """;
            seed.Parameters.AddWithValue("$id", "R01-OPS-P" + i);
            seed.Parameters.AddWithValue("$assignment", "R01-OPS-A" + i);
            seed.Parameters.AddWithValue("$name", "فرد عمليات اختباري " + i);
            seed.Parameters.AddWithValue("$number", "09200" + i);
            seed.Parameters.AddWithValue("$start", DateOnly.Parse(Today).AddDays(-1).ToString("yyyy-MM-dd"));
            await seed.ExecuteNonQueryAsync();
        }
    }

    private async Task<BridgeResponse> Dispatch(string action, JsonObject? payload = null)
    {
        payload ??= new JsonObject();
        if (ActionManifest.Find(action)!.IsMutating && !payload.ContainsKey("operationId"))
            payload["operationId"] = Guid.NewGuid().ToString("N");
        var result = await _dispatcher.DispatchAsync(new JsonObject {
            ["requestId"] = Guid.NewGuid().ToString("N"), ["protocolVersion"] = BridgeProtocol.ProtocolVersion,
            ["action"] = action, ["sessionNonce"] = _nonce, ["payload"] = payload.DeepClone()
        }.ToJsonString(), LocalWebUiOrigin.BaseUrl, true, nonce => _services.ResolveSessionAsync(nonce));
        R01ResponseSchema.AssertMatches(result.Response, action);
        return result.Response;
    }

    private async Task<BridgeResponse> Call(string action, JsonObject? payload = null)
    {
        var response = await Dispatch(action, payload);
        Assert.True(response.Success, action + ": " + response.UserMessage);
        R01WireShapeSnapshot.Verify(action, response);
        _output.WriteLine("R01_NATIVE_SHAPE " + action + " " + R01WireShapeSnapshot.Shape(response));
        return response;
    }
    private async Task<JsonObject> Object(string action, JsonObject? payload = null)
        => Assert.IsType<JsonObject>((await Call(action, payload)).Data);
    private string Scalar(string sql, string? id = null)
    {
        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }
    private long AuditCount() => long.Parse(Scalar("SELECT COUNT(*) FROM AuditEvents;"));
    private async Task Replay(string action, JsonObject intent, JsonObject original)
    {
        long before = AuditCount();
        Assert.True(JsonNode.DeepEquals(original, await Object(action, intent)));
        Assert.Equal(before, AuditCount());
    }
    private JsonObject DayFilter() => new() { ["businessDate"] = Today, ["siteId"] = Source, ["pageSize"] = 50 };
    private JsonObject MonthFilter(string site = Source) => new() {
        ["year"] = DateOnly.Parse(Today).Year, ["month"] = DateOnly.Parse(Today).Month,
        ["siteId"] = site, ["archiveMode"] = true, ["pageSize"] = 50
    };
    private static JsonObject Row(JsonObject data, string id) => Assert.Single(
        Assert.IsType<JsonArray>(data["items"]).OfType<JsonObject>(), x => x["personId"]!.GetValue<string>() == id);
    private JsonObject Attendance(string mode, string status) => new() {
        ["mode"] = mode, ["statusId"] = status, ["businessDate"] = Today,
        ["siteId"] = Source, ["personIds"] = new JsonArray(P1), ["reason"] = "تصحيح حضور اختباري موثق"
    };

    [Fact]
    public async Task Attendance_DraftPreviewCommitRevise_RealReadModelsAndHistoricalAliasesAgree()
    {
        long startAudit = AuditCount();
        Assert.Equal(3L, (await Object("QueryUnprepared", DayFilter()))["total"]!.GetValue<long>());
        var draft = await Object("SaveAttendanceDraft", new JsonObject {
            ["businessDate"] = Today, ["siteId"] = Source, ["targetStatusId"] = "STS-PRESENT",
            ["selectedPersonIds"] = new JsonArray(P1)
        });
        var drafts = Assert.IsType<JsonArray>((await Call("ListAttendanceDrafts")).Data);
        Assert.Equal(P1, Assert.Single(drafts)!["selectedPersonIds"]![0]!.GetValue<string>());
        await Object("DeleteAttendanceDraft", new JsonObject { ["draftId"] = draft["draftId"]!.DeepClone() });
        Assert.Empty(Assert.IsType<JsonArray>((await Call("ListAttendanceDrafts")).Data));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM AttendanceRecords;"));
        Assert.False((await Dispatch("PreviewAttendanceChange", Attendance("initial", "STS-LEAVE"))).Success);
        Assert.Equal(startAudit, AuditCount());
        var preview = await Object("PreviewAttendanceChange", Attendance("initial", "STS-PRESENT"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM AttendanceRecords;"));
        var intent = new JsonObject { ["previewToken"] = preview["previewToken"]!.DeepClone() };
        var saved = await Object("SubmitAttendance", intent);
        await Replay("SubmitAttendance", intent, saved);
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM AttendanceRecords;"));
        Assert.Equal("STS-PRESENT", Scalar("SELECT StatusId FROM AttendanceRecords WHERE PersonId=$id;", P1));
        var reused = (JsonObject)intent.DeepClone();
        reused["operationId"] = Guid.NewGuid().ToString("N");
        Assert.False((await Dispatch("SubmitAttendance", reused)).Success);
        var revisePreview = await Object("PreviewAttendanceChange", Attendance("revise", "STS-ABSENT"));
        var reviseIntent = new JsonObject { ["previewToken"] = revisePreview["previewToken"]!.DeepClone() };
        var revised = await Object("ReviseTodayAttendance", reviseIntent);
        await Replay("ReviseTodayAttendance", reviseIntent, revised);
        Assert.Equal("STS-ABSENT", Scalar("SELECT StatusId FROM AttendanceRecords WHERE PersonId=$id;", P1));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM AttendanceRevisions;"));
        Assert.Equal(startAudit + 2, AuditCount());
        var dailyFilter = DayFilter(); dailyFilter["statusId"] = "STS-ABSENT";
        Assert.Equal(P1, Row(await Object("QueryDailyRoster", dailyFilter), P1)["personId"]!.GetValue<string>());
        var remaining = await Object("QueryUnprepared", DayFilter());
        Assert.Equal(2L, remaining["total"]!.GetValue<long>());
        Assert.DoesNotContain(Assert.IsType<JsonArray>(remaining["items"]), x => x!["personId"]!.GetValue<string>() == P1);
        Assert.Equal(3L, (await Object("GetDashboardSummary", DayFilter()))["totalForce"]!.GetValue<long>());
        var detail = await Object("GetMonthlyCellDetails", new JsonObject {
            ["personId"] = P1, ["businessDate"] = Today, ["siteId"] = Source
        });
        Assert.Equal("غياب", detail["attendance"]!["statusName"]!.GetValue<string>());
        Assert.Single(Assert.IsType<JsonArray>(detail["revisions"]));
        var monthly = await Call("QueryMonthlyGrid", MonthFilter());
        var month = Assert.IsType<JsonObject>(monthly.Data);
        Assert.Equal("غياب", Row(month, P1)["days"]![Today]!["name"]!.GetValue<string>());
        string shape = R01WireShapeSnapshot.Shape(monthly);
        Assert.DoesNotContain(Today, shape);
        // A corrupted actual native map must fail, rather than be normalized away.
        var corrupted = (JsonObject)month.DeepClone();
        Row(corrupted, P1)["days"] = new JsonObject { ["not-a-date"] = new JsonObject() };
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => R01WireShapeSnapshot.Shape(monthly with { Data = corrupted }));
        var wrongCellType = (JsonObject)month.DeepClone();
        Row(wrongCellType, P1)["days"]![Today] = 42;
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => R01WireShapeSnapshot.Verify("QueryMonthlyGrid", monthly with { Data = wrongCellType }));
        Assert.Contains(Assert.IsType<JsonArray>((await Call("GetAvailableArchivePeriods")).Data), x => x!.GetValue<string>() == Today[..7]);

        var filters = new JsonObject { ["personId"] = P1, ["dateFrom"] = Today, ["dateTo"] = Today };
        var report = await Object("BuildPersonLifetimeReport", filters);
        Assert.Equal(1L, report["attendanceTotal"]!.GetValue<long>());
        Assert.Equal("غياب", Assert.Single(Assert.IsType<JsonArray>(report["attendance"]))!["statusName"]!.GetValue<string>());
        Assert.Equal(1L, report["revisionTotal"]!.GetValue<long>());
        foreach (string alias in new[] { "QueryPersonAttendanceTimeline", "QueryPersonAttendanceTotals", "QueryPersonOperationsTimeline", "QueryPersonMovements" })
            Assert.True(JsonNode.DeepEquals(report, await Object(alias, filters)));
        var navigation = await Object("GetNavigationPermissions");
        Assert.Equal("GeneralAdministration", navigation["role"]!.GetValue<string>());
        Assert.Contains(Assert.IsType<JsonArray>(navigation["allowedActions"]), x => x!.GetValue<string>() == "SubmitAttendance");
        var version = await Object("GetApplicationVersion");
        Assert.True(version["offline"]!.GetValue<bool>());
        Assert.Equal(BridgeProtocol.ProtocolVersion, version["bridgeProtocol"]!.GetValue<int>());
        Assert.Contains(Assert.IsType<JsonArray>((await Call("QueryAttendanceStatuses")).Data), x => x!["statusId"]!.GetValue<string>() == "STS-ABSENT");
        foreach (string search in new[] { "SearchPersons", "SearchActivePersonsForAccount" })
        {
            var results = Assert.IsType<JsonArray>((await Object(search, new JsonObject { ["term"] = "٠٩٢٠٠١", ["take"] = 1 }))["items"]);
            Assert.Equal(P1, Assert.Single(results)!["personId"]!.GetValue<string>());
        }
        var auto = await Object("GetAutoAttendanceRun");
        Assert.False(auto["todayReceiptPresent"]!.GetValue<bool>());
        Assert.Empty(Assert.IsType<JsonArray>(auto["runs"]));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM AutoAttendanceRuns;"));
        Assert.Equal(startAudit + 2, AuditCount());
        Assert.True(_db.HasValidIntegrity());
    }

    private JsonObject Transfer(string id, bool external) => new() {
        ["businessDate"] = Today, ["sourceSiteId"] = Source,
        ["destinationSiteId"] = external ? null : Destination,
        ["destinationExternalName"] = external ? "جهة خارجية اختبارية" : null,
        ["personIds"] = new JsonArray(id)
    };

    [Fact]
    public async Task Transfers_RealInternalAndExternalCommits_PreserveDatedMembershipAndArchives()
    {
        long before = AuditCount();
        foreach (bool external in new[] { false, true })
        {
            string id = external ? P2 : P1;
            string previewAction = external ? "PreviewExternalTransfer" : "PreviewInternalTransfer";
            string commitAction = external ? "CommitExternalTransfer" : "CommitInternalTransfer";
            var preview = await Object(previewAction, Transfer(id, external));
            Assert.Equal(external ? "1" : "0", Scalar("SELECT COUNT(*) FROM PersonnelMovements;"));
            var intent = new JsonObject { ["previewToken"] = preview["previewToken"]!.DeepClone() };
            var saved = await Object(commitAction, intent);
            await Replay(commitAction, intent, saved);
            var reused = (JsonObject)intent.DeepClone(); reused["operationId"] = Guid.NewGuid().ToString("N");
            Assert.False((await Dispatch(commitAction, reused)).Success);
        }
        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM PersonnelMovements;"));
        Assert.Equal(before + 2, AuditCount());
        Assert.Equal(Today, Scalar("SELECT ValidToLocalDate FROM PersonAssignments WHERE AssignmentId='R01-OPS-A1';"));
        // Live membership changes immediately; transfer-day attendance remains forbidden.
        Assert.Equal(Today, Scalar("SELECT ValidFromLocalDate FROM PersonAssignments WHERE PersonId=$id AND ValidToLocalDate IS NULL;", P1));
        Assert.Equal(Destination, Scalar("SELECT SiteId FROM PersonAssignments WHERE PersonId=$id AND ValidToLocalDate IS NULL;", P1));
        Assert.Equal("2", Scalar("SELECT OrganizationalStatus FROM Persons WHERE PersonId=$id;", P2));
        Assert.Equal("0", Scalar("SELECT ActiveFlag FROM Persons WHERE PersonId=$id;", P2));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM AttendanceRecords;"));
        var live = await Object("QueryActivePersonsBySite", new JsonObject { ["includeArchived"] = true, ["organizationalStatus"] = 4 });
        Assert.Equal(2L, live["total"]!.GetValue<long>());
        Assert.DoesNotContain(Assert.IsType<JsonArray>(live["items"]), x => x!["personId"]!.GetValue<string>() == P2);
        foreach (bool external in new[] { false, true })
        {
            string id = external ? P2 : P1;
            var archive = await Object("QueryMovementArchive", new JsonObject {
                ["siteId"] = external ? Source : Destination, ["type"] = external ? "external" : "internal",
                ["dateFrom"] = Today, ["dateTo"] = Today, ["pageSize"] = 1
            });
            Assert.Equal(1L, archive["total"]!.GetValue<long>());
            var entry = Row(archive, id);
            var details = await Object("GetMovementDetails", new JsonObject { ["movementId"] = entry["movementId"]!.DeepClone() });
            Assert.Equal(id, details["personId"]!.GetValue<string>());
            Assert.Equal(external ? "خارجي" : "داخلي", details["type"]!.GetValue<string>());
            Assert.Equal(external ? "جهة خارجية اختبارية" : "موقع وجهة اختباري", details["destination"]!.GetValue<string>());
            if (external) Assert.Null(details["destinationSiteId"]);
            else Assert.Equal(Destination, details["destinationSiteId"]!.GetValue<string>());
        }
        var month = await Object("QueryMonthlyGrid", MonthFilter());
        Assert.Equal("نقل (د)", Row(month, P1)["days"]![Today]!["symbol"]!.GetValue<string>());
        Assert.Equal("نقل (خ)", Row(month, P2)["days"]![Today]!["symbol"]!.GetValue<string>());
        var report = await Object("QueryPersonMovements", new JsonObject { ["personId"] = P2, ["dateFrom"] = Today, ["dateTo"] = Today });
        Assert.Equal(1L, report["movementTotal"]!.GetValue<long>());
        Assert.Equal(0L, report["attendanceTotal"]!.GetValue<long>());
        var sameDay = Attendance("initial", "STS-PRESENT"); sameDay["siteId"] = Destination;
        var blocked = await Dispatch("PreviewAttendanceChange", sameDay);
        Assert.False(blocked.Success);
        Assert.Equal(BridgeErrorCodes.TransferDayAttendanceForbidden, blocked.ErrorCode);
        Assert.Equal(before + 2, AuditCount());
        // Advance the native business clock, then authenticate a NEW real session.
        // Reusing yesterday's nonce would only test an expired session rejection.
        _clock.AdvanceSeconds(24 * 60 * 60);
        var login = await Object("AuthenticateLocalUser", new JsonObject {
            ["username"] = "r01-operations-owner", ["password"] = "Native123"
        });
        _nonce = login["sessionNonce"]!.GetValue<string>();
        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='AUTH_LOGIN_SUCCESS';"));
        long afterAuthentication = AuditCount();
        Assert.Equal(before + 3, afterAuthentication); // Two transfers and one intentional new login.
        var nextDay = Attendance("initial", "STS-PRESENT"); nextDay["siteId"] = Destination;
        var eligible = await Object("PreviewAttendanceChange", nextDay);
        Assert.Equal(P1, Assert.Single(Assert.IsType<JsonArray>(eligible["persons"]))!["personId"]!.GetValue<string>());
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM AttendanceRecords;"));
        Assert.Equal(afterAuthentication, AuditCount());
        Assert.True(_db.HasValidIntegrity());
    }

    [Fact]
    public async Task Separation_RealServerDelayCommit_RetainsReasonAndHistoryWithoutInventedAttendance()
    {
        long before = AuditCount();
        var preview = await Object("PreviewSeparation", new JsonObject {
            ["personId"] = P1, ["reason"] = "سبب فصل اختباري موثق", ["businessDate"] = Today
        });
        Assert.Equal(5, preview["minimumConfirmationSeconds"]!.GetValue<int>());
        Assert.Equal(before, AuditCount());
        var intent = new JsonObject { ["previewToken"] = preview["previewToken"]!.DeepClone() };
        Assert.False((await Dispatch("CommitSeparation", intent)).Success);
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM PersonnelMovements;"));
        _clock.AdvanceSeconds(6);
        var saved = await Object("CommitSeparation", intent);
        await Replay("CommitSeparation", intent, saved);
        Assert.True(saved["separated"]!.GetValue<bool>());
        Assert.Equal("3", Scalar("SELECT OrganizationalStatus FROM Persons WHERE PersonId=$id;", P1));
        Assert.Equal("0", Scalar("SELECT ActiveFlag FROM Persons WHERE PersonId=$id;", P1));
        Assert.Equal(Today, Scalar("SELECT ValidToLocalDate FROM PersonAssignments WHERE AssignmentId='R01-OPS-A1';"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM PersonnelMovements;"));
        Assert.Equal("0", Scalar("SELECT COUNT(*) FROM AttendanceRecords;"));
        Assert.Equal(before + 1, AuditCount());
        var archive = await Object("QuerySeparationArchive", new JsonObject { ["term"] = "092001", ["pageSize"] = 1 });
        Assert.Equal(1L, archive["total"]!.GetValue<long>());
        var row = Row(archive, P1);
        var details = await Object("GetSeparationDetails", new JsonObject { ["movementId"] = row["movementId"]!.DeepClone() });
        Assert.Equal(P1, details["personId"]!.GetValue<string>());
        Assert.Equal("سبب فصل اختباري موثق", details["reason"]!.GetValue<string>());
        var month = await Object("QueryMonthlyGrid", MonthFilter());
        Assert.Equal("separation", Row(month, P1)["days"]![Today]!["kind"]!.GetValue<string>());
        Assert.Equal(2L, (await Object("GetDashboardSummary", DayFilter()))["totalForce"]!.GetValue<long>());
        Assert.Equal(before + 1, AuditCount());
        Assert.True(_db.HasValidIntegrity());
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() { _db?.Dispose(); SyntheticFixtures.DeleteTempDatabase(_path); }
}
