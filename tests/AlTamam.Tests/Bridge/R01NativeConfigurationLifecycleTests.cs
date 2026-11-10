using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace AlTamam.Tests.Bridge;

// Real native lifecycle coverage, not a replacement registration table or mock
// role matrix. Each fact owns an isolated encrypted database and real session.
public sealed class R01NativeConfigurationLifecycleTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "AlTamam-R01-" + Guid.NewGuid().ToString("N"));
    private AlTamamDbContext _db = null!;
    private BridgeSharedServices _services = null!;
    private WebView2BridgeDispatcher _dispatcher = null!;
    private readonly ITestOutputHelper _output;
    public R01NativeConfigurationLifecycleTests(ITestOutputHelper output) => _output = output;
    private string _nonce = new('0', 32);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex, useConnectionPooling: false);
        await _db.InitializeAsync();
        _services = new BridgeSharedServices(_db);
        _dispatcher = BridgeHandlerCatalog.Create(_services);
        Assert.Equal(ActionManifest.All.Select(x => x.Action).OrderBy(x => x, StringComparer.Ordinal),
            _dispatcher.RegisteredActions.OrderBy(x => x, StringComparer.Ordinal));
        Assert.False((await Object("GetProvisioningState"))["provisioned"]!.GetValue<bool>());
        var login = await Object("AuthenticateLocalUser", new JsonObject {
            ["username"] = "r01-lifecycle-owner", ["password"] = "Native123",
            ["passwordConfirmation"] = "Native123", ["setupIntent"] = "firstTimeSetup"
        });
        _nonce = login["sessionNonce"]!.GetValue<string>();
        Assert.NotNull(await _services.ResolveSessionAsync(_nonce));
        Assert.True((await Object("GetProvisioningState"))["provisioned"]!.GetValue<bool>());
    }

    private async Task<BridgeResponse> Dispatch(string action, JsonObject? payload = null)
    {
        payload ??= new JsonObject();
        if (ActionManifest.Find(action)!.IsMutating && action != "Logout" && !payload.ContainsKey("operationId"))
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

    private long AuditCount() => long.Parse(Scalar("SELECT COUNT(*) FROM AuditEvents;"),
        System.Globalization.CultureInfo.InvariantCulture);

    private async Task ReplayWithoutNewAudit(string action, JsonObject intent, JsonObject original)
    {
        long before = AuditCount();
        var replay = await Object(action, intent);
        Assert.True(JsonNode.DeepEquals(original, replay), action + " changed its stored receipt");
        Assert.Equal(before, AuditCount());
    }

    [Fact]
    public async Task SitesAndStatuses_CreateRenameRetireAndDeactivate_PreserveHistoryAndReceipts()
    {
        // Independent earlier records permit a meaningful effective-date rename.
        // Do not rewrite today's immutable history just to make a fixture pass.
        using (var connection = _db.CreateOpenConnection())
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO Sites(SiteId,VisibleName,NormalizedName,IsSystemSite,ActiveFlag,CreatedAtUtc,RowVersion)
                VALUES('SITE-R01-OLD','موقع قديم اختباري','موقع قديم اختباري',0,1,'2026-01-01T00:00:00Z',1);
                INSERT INTO SiteNameHistory(SiteId,VisibleNameSnapshot,ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc)
                VALUES('SITE-R01-OLD','موقع قديم اختباري','2026-01-01',NULL,'USR-SYSTEM','2026-01-01T00:00:00Z');
                INSERT INTO AttendanceStatusDefinitions(StatusId,VisibleName,NormalizedName,ShortSymbol,ChosenColorHex,
                    IsSystemSeeded,ActiveFlag,SortOrder,CreatedAtUtc,RowVersion)
                VALUES('STS-R01-OLD','حالة قديمة اختبارية','حاله قديمه اختباريه','R0','#112233',0,1,90,'2026-01-01T00:00:00Z',1);
                INSERT INTO AttendanceStatusNameHistory(StatusId,VisibleNameSnapshot,ShortSymbolSnapshot,ColorSnapshot,
                    ValidFromLocalDate,ValidToLocalDate,RecordedAtUtc)
                VALUES('STS-R01-OLD','حالة قديمة اختبارية','R0','#112233','2026-01-01',NULL,'2026-01-01T00:00:00Z');
                """;
            await seed.ExecuteNonQueryAsync();
        }
        var siteIntent = new JsonObject { ["name"] = "موقع إنشاء المرسل" };
        var site = await Object("CreateSite", siteIntent);
        string siteId = site["siteId"]!.GetValue<string>();
        Assert.Equal("موقع إنشاء المرسل", Scalar("SELECT VisibleName FROM Sites WHERE SiteId=$id;", siteId));
        await ReplayWithoutNewAudit("CreateSite", siteIntent, site);
        var renamed = new JsonObject { ["siteId"] = "SITE-R01-OLD", ["name"] = "موقع بعد التسمية", ["expectedRevision"] = 1L };
        var rename = await Object("RenameSite", renamed);
        await ReplayWithoutNewAudit("RenameSite", renamed, rename);
        Assert.Equal("موقع بعد التسمية", Scalar("SELECT VisibleName FROM Sites WHERE SiteId=$id;", "SITE-R01-OLD"));
        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM SiteNameHistory WHERE SiteId=$id;", "SITE-R01-OLD"));
        var sites = Assert.IsType<JsonArray>((await Call("QuerySites")).Data);
        Assert.Contains(sites, row => row!["siteId"]!.GetValue<string>() == siteId);
        var retirement = new JsonObject { ["siteId"] = siteId, ["expectedRevision"] = 1L,
            ["expectedActiveCount"] = 0, ["confirmed"] = true };
        var retired = await Object("RemoveSiteAndReassignCurrentPersons", retirement);
        Assert.Equal(0, retired["reassignedCount"]!.GetValue<int>());
        await ReplayWithoutNewAudit("RemoveSiteAndReassignCurrentPersons", retirement, retired);
        Assert.Equal("0", Scalar("SELECT ActiveFlag FROM Sites WHERE SiteId=$id;", siteId));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM SiteNameHistory WHERE SiteId=$id;", siteId));

        var statusIntent = new JsonObject { ["name"] = "حالة إنشاء المرسل", ["symbol"] = "NX", ["color"] = "#2277DD" };
        var status = await Object("CreateAttendanceStatus", statusIntent);
        string statusId = status["statusId"]!.GetValue<string>();
        await ReplayWithoutNewAudit("CreateAttendanceStatus", statusIntent, status);
        Assert.Equal("حالة إنشاء المرسل", Scalar("SELECT VisibleName FROM AttendanceStatusDefinitions WHERE StatusId=$id;", statusId));
        var editStatus = new JsonObject { ["statusId"] = "STS-R01-OLD", ["name"] = "حالة بعد التعديل",
            ["symbol"] = "RY", ["color"] = "#557799", ["expectedRevision"] = 1L };
        var edited = await Object("UpdateAttendanceStatus", editStatus);
        await ReplayWithoutNewAudit("UpdateAttendanceStatus", editStatus, edited);
        Assert.Equal("#557799", Scalar("SELECT ChosenColorHex FROM AttendanceStatusDefinitions WHERE StatusId=$id;", "STS-R01-OLD"));
        Assert.Equal("2", Scalar("SELECT COUNT(*) FROM AttendanceStatusNameHistory WHERE StatusId=$id;", "STS-R01-OLD"));
        var deactivate = new JsonObject { ["statusId"] = statusId, ["expectedRevision"] = 1L };
        var inactive = await Object("DeactivateOrDeleteStatus", deactivate);
        await ReplayWithoutNewAudit("DeactivateOrDeleteStatus", deactivate, inactive);
        var active = Assert.IsType<JsonArray>((await Call("GetActiveAttendanceStatuses")).Data);
        Assert.DoesNotContain(active, row => row!["statusId"]!.GetValue<string>() == statusId);
        Assert.Equal("0", Scalar("SELECT ActiveFlag FROM AttendanceStatusDefinitions WHERE StatusId=$id;", statusId));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM AttendanceStatusNameHistory WHERE StatusId=$id;", statusId));
        Assert.True(_db.HasValidIntegrity());
    }

    [Fact]
    public async Task CustomColumns_RealCreateEditDeactivate_PreserveStableIdAndStoredValues()
    {
        var person = await Object("CreatePerson", new JsonObject { ["visibleName"] = "فرد حفظ القيمة",
            ["serviceNumberA"] = "981001", ["siteId"] = "SITE-UNSPECIFIED" });
        string personId = person["personId"]!.GetValue<string>();
        var intent = new JsonObject { ["caption"] = "حقل دورة المرسل", ["fieldType"] = 1 };
        var column = await Object("CreateCustomFieldDefinition", intent);
        string id = column["columnId"]!.GetValue<string>();
        await ReplayWithoutNewAudit("CreateCustomFieldDefinition", intent, column);
        // Execute the newly enabled action through the REAL authenticated dispatcher,
        // rather than seeding the value directly with SQL. This is mandatory R01
        // native evidence for both the wire shape and persisted audit trail.
        var saveValueIntent = new JsonObject {
            ["personId"] = personId,
            ["expectedRevision"] = person["revision"]!.DeepClone(),
            ["values"] = new JsonObject { [id] = "007" }
        };
        long previousAuditCount = AuditCount();
        var savedValue = await Object("UpdateCustomFieldValues", saveValueIntent);
        Assert.Equal(personId, savedValue["personId"]!.GetValue<string>());
        Assert.Equal(person["revision"]!.GetValue<long>() + 1,
            savedValue["revision"]!.GetValue<long>());
        Assert.Equal(1, savedValue["changedCount"]!.GetValue<int>());
        Assert.Equal(previousAuditCount + 1, AuditCount());
        Assert.Equal("007", Scalar(
            "SELECT FieldValueText FROM CustomFieldValues WHERE PersonId=$id;", personId));
        Assert.Equal("1", Scalar(
            "SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='CUSTOM_FIELD_VALUE_UPDATE' AND TargetEntityId=$id;",
            personId));
        await ReplayWithoutNewAudit("UpdateCustomFieldValues", saveValueIntent, savedValue);
        var staleValue = (JsonObject)saveValueIntent.DeepClone();
        staleValue["operationId"] = Guid.NewGuid().ToString("N");
        var staleReply = await Dispatch("UpdateCustomFieldValues", staleValue);
        Assert.False(staleReply.Success);
        Assert.Equal(BridgeErrorCodes.ConflictStale, staleReply.ErrorCode);
        Assert.Equal("007", Scalar(
            "SELECT FieldValueText FROM CustomFieldValues WHERE PersonId=$id;", personId));
        var columns = await Object("GetCustomFieldDefinitions");
        var current = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(columns["items"]),
            row => row!["columnId"]!.GetValue<string>() == id));
        var edit = new JsonObject { ["columnId"] = id, ["expectedSnapshot"] = current["snapshot"]!.GetValue<string>(),
            ["caption"] = "حقل بعد التعديل", ["fieldType"] = 1, ["displayOrder"] = current["displayOrder"]!.GetValue<int>(), ["visible"] = false };
        var hidden = await Object("UpdateCustomFieldMetadata", edit);
        await ReplayWithoutNewAudit("UpdateCustomFieldMetadata", edit, hidden);
        Assert.Equal(id, hidden["columnId"]!.GetValue<string>());
        Assert.Equal(column["technicalKey"]!.GetValue<string>(), hidden["technicalKey"]!.GetValue<string>());
        Assert.False(hidden["visible"]!.GetValue<bool>());
        var retire = new JsonObject { ["columnId"] = id, ["expectedSnapshot"] = hidden["snapshot"]!.GetValue<string>(), ["confirmed"] = true };
        var retired = await Object("DeactivateCustomField", retire);
        await ReplayWithoutNewAudit("DeactivateCustomField", retire, retired);
        Assert.Equal("007", Scalar("SELECT FieldValueText FROM CustomFieldValues WHERE FieldDefinitionId=$id;", id));
        Assert.Equal("0", Scalar("SELECT ActiveFlag FROM CustomFieldDefinitions WHERE FieldDefinitionId=$id;", id));
        var all = await Object("GetCustomFieldDefinitions", new JsonObject { ["includeInactive"] = true });
        Assert.Single(Assert.IsType<JsonArray>(all["items"]), row => row!["columnId"]!.GetValue<string>() == id);
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM AuditEvents WHERE TargetEntityId=$id AND OperationCode='ROSTER_COLUMN_DEACTIVATE';", id));
        Assert.True(_db.HasValidIntegrity());
    }

    [Fact]
    public async Task IndependentSettings_RealWritesAndReads_KeepSeparateSchedulesAndReceipts()
    {
        await Object("SetOutputDirectory", new JsonObject { ["directory"] = _folder });
        await Object("SetTheme", new JsonObject { ["theme"] = "dark" });
        var due = new JsonObject { ["dueTimeLocal"] = "09:15" };
        var dueSaved = await Object("SetAttendanceSchedule", due);
        await ReplayWithoutNewAudit("SetAttendanceSchedule", due, dueSaved);
        await Object("SetAttendanceSchedule", new JsonObject { ["dailyLockTimeLocal"] = "21:30" });
        await Object("SetAutoAttendanceDefaultStatus", new JsonObject { ["statusId"] = "STS-ABSENT" });
        var backup = new JsonObject { ["enabled"] = true, ["backupTime"] = "16:45", ["retentionCount"] = 7 };
        var backupSaved = await Object("UpdateBackupSchedule", backup);
        await ReplayWithoutNewAudit("UpdateBackupSchedule", backup, backupSaved);
        var monthly = new JsonObject { ["enabled"] = false, ["directory"] = _folder, ["exportTime"] = "22:00" };
        var monthlySaved = await Object("ConfigureAutoMonthlyExcel", monthly);
        await ReplayWithoutNewAudit("ConfigureAutoMonthlyExcel", monthly, monthlySaved);
        var policy = await Object("GetAutoAttendancePolicy");
        Assert.Equal("09:15", policy["dueTimeLocal"]!.GetValue<string>());
        Assert.Equal("21:30", policy["dailyLockTimeLocal"]!.GetValue<string>());
        Assert.Equal("STS-ABSENT", policy["defaultStatusId"]!.GetValue<string>());
        var overview = await Object("GetSettingsOverview");
        Assert.Equal("dark", overview["theme"]!.GetValue<string>());
        Assert.Equal(_folder, overview["backupDirectory"]!.GetValue<string>());
        Assert.Equal("16:45", overview["backupTime"]!.GetValue<string>());
        Assert.False(overview["autoMonthlyExcel"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("22:00", overview["autoMonthlyExcel"]!["exportTime"]!.GetValue<string>());
        var schedule = await Object("GetBackupSchedule");
        Assert.True(schedule["enabled"]!.GetValue<bool>());
        Assert.Equal(7, schedule["retentionCount"]!.GetValue<int>());
        var integrity = await Object("RunDatabaseIntegrityCheck");
        Assert.Equal("ok", integrity["integrity"]!.GetValue<string>());
        Assert.True(integrity["encrypted"]!.GetValue<bool>());
        Assert.Equal("dark", Scalar("SELECT SettingValue FROM AppSettings WHERE SettingKey='Theme';"));
        Assert.Equal("09:15", Scalar("SELECT SettingValue FROM AppSettings WHERE SettingKey='AutoAttendanceDueTimeLocal';"));
        Assert.Equal("21:30", Scalar("SELECT SettingValue FROM AppSettings WHERE SettingKey='DailyAttendanceLockTimeLocal';"));
        Assert.Equal("16:45", Scalar("SELECT SettingValue FROM AppSettings WHERE SettingKey='AutoBackupTimeLocal';"));
    }

    [Fact]
    public async Task UserAccounts_RealLifecycle_RetainsPersonLinkAndOneAuditPerMutation()
    {
        var person = await Object("CreatePerson", new JsonObject { ["visibleName"] = "فرد حساب المرسل",
            ["serviceNumberA"] = "981002", ["siteId"] = "SITE-UNSPECIFIED" });
        string personId = person["personId"]!.GetValue<string>();
        var roles = Assert.IsType<JsonArray>((await Call("GetAllowedRoles")).Data);
        Assert.Contains(roles, row => row!["role"]!.GetValue<string>() == "Supervisor");
        var create = new JsonObject { ["username"] = "r01-linked-user", ["password"] = "Native123",
            ["passwordConfirmation"] = "Native123", ["role"] = "Supervisor", ["personId"] = personId };
        var user = await Object("CreateUserAccount", create);
        string id = user["userId"]!.GetValue<string>();
        await ReplayWithoutNewAudit("CreateUserAccount", create, user);
        var password = new JsonObject { ["userId"] = id, ["expectedRevision"] = 1L, ["password"] = "Changed456" };
        var changed = await Object("ChangeUserPassword", password);
        await ReplayWithoutNewAudit("ChangeUserPassword", password, changed);
        var deactivate = new JsonObject { ["userId"] = id, ["expectedRevision"] = 2L };
        var inactive = await Object("DeactivateUserAccount", deactivate);
        await ReplayWithoutNewAudit("DeactivateUserAccount", deactivate, inactive);
        Assert.Equal("0", Scalar("SELECT ActiveFlag FROM Users WHERE UserId=$id;", id));
        var reactivate = new JsonObject { ["userId"] = id, ["expectedRevision"] = 3L, ["reason"] = "مراجعة حساب اختباري مصرح بها" };
        var active = await Object("ReactivateUserAccount", reactivate);
        await ReplayWithoutNewAudit("ReactivateUserAccount", reactivate, active);
        Assert.Equal("1", Scalar("SELECT ActiveFlag FROM Users WHERE UserId=$id;", id));
        Assert.Equal("4", Scalar("SELECT RowVersion FROM Users WHERE UserId=$id;", id));
        Assert.Equal(personId, Scalar("SELECT LinkedPersonId FROM Users WHERE UserId=$id;", id));
        foreach (string code in new[] { "USER_CREATE", "USER_PASSWORD_CHANGE", "USER_DEACTIVATE", "USER_REACTIVATE" })
            Assert.Equal("1", Scalar("SELECT COUNT(*) FROM AuditEvents WHERE TargetEntityId=$id AND OperationCode='" + code + "';", id));
        var users = await Object("ListUserAccounts", new JsonObject { ["term"] = "r01-linked-user" });
        var listed = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(users["items"])));
        Assert.Equal(id, listed["userId"]!.GetValue<string>());
        Assert.Equal(personId, listed["personId"]!.GetValue<string>());
        Assert.Equal(4L, listed["rowVersion"]!.GetValue<long>());
        await Call("Logout");
        var login = await Object("AuthenticateLocalUser", new JsonObject { ["username"] = "r01-linked-user", ["password"] = "Changed456" });
        _nonce = login["sessionNonce"]!.GetValue<string>();
        Assert.Equal("Supervisor", login["role"]!.GetValue<string>());
        Assert.Equal(id, (await Object("GetCurrentSession"))["userId"]!.GetValue<string>());
        long beforeDenied = AuditCount();
        foreach (string action in new[] { "SetAttendanceSchedule", "UpdateBackupSchedule",
            "ConfigureAutoMonthlyExcel", "SetAutoAttendanceDefaultStatus", "CreateUserAccount",
            "ChangeUserPassword", "ReactivateUserAccount", "UpdateAuditPresentation", "DeleteAuditPresentation" })
        {
            var denied = await Dispatch(action);
            Assert.False(denied.Success);
            Assert.Equal(BridgeErrorCodes.Unauthorized, denied.ErrorCode);
            Assert.Null(denied.Data);
            Assert.Null(denied.AuditEventId);
            Assert.True(denied.InvalidationEvents is null || denied.InvalidationEvents.Count == 0);
        }
        Assert.Equal(beforeDenied, AuditCount());
        Assert.Equal("4", Scalar("SELECT RowVersion FROM Users WHERE UserId=$id;", id));
        Assert.True(_db.HasValidIntegrity());
    }

    [Fact]
    public async Task AuditPresentation_RealEditHideAndHistory_PreserveCanonicalEvidence()
    {
        var site = await Object("CreateSite", new JsonObject { ["name"] = "موقع عرض التدقيق" });
        var audit = await Object("QueryAuditLog", new JsonObject {
            ["operation"] = "SITE_CREATE", ["target"] = site["siteId"]!.GetValue<string>()
        });
        var original = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(audit["items"])));
        string id = original["auditEventId"]!.GetValue<string>();
        string description = original["rawDescription"]!.GetValue<string>();
        string hash = original["eventHash"]!.GetValue<string>();
        var edit = new JsonObject { ["auditEventId"] = id, ["expectedOverlayVersion"] = 0,
            ["correctedDescription"] = "وصف عرض اختباري مصحح", ["reason"] = "تصحيح العرض مع حفظ الدليل الأصلي" };
        var edited = await Object("UpdateAuditPresentation", edit);
        await ReplayWithoutNewAudit("UpdateAuditPresentation", edit, edited);
        Assert.Equal(description, edited["beforeState"]!["displayDescription"]!.GetValue<string>());
        Assert.Equal("وصف عرض اختباري مصحح", edited["afterState"]!["displayDescription"]!.GetValue<string>());
        var hide = new JsonObject { ["auditEventId"] = id, ["expectedOverlayVersion"] = 1,
            ["reason"] = "إخفاء العرض الاختباري دون حذف الأدلة" };
        var hidden = await Object("DeleteAuditPresentation", hide);
        await ReplayWithoutNewAudit("DeleteAuditPresentation", hide, hidden);
        Assert.True(hidden["afterState"]!["isTombstoned"]!.GetValue<bool>());
        var details = await Object("GetAuditEntryDetails", new JsonObject { ["auditEventId"] = id });
        Assert.Equal(description, details["rawDescription"]!.GetValue<string>());
        Assert.Equal(hash, details["eventHash"]!.GetValue<string>());
        Assert.True(details["chainValid"]!.GetValue<bool>());
        Assert.Equal(2, Assert.IsType<JsonArray>(details["overlays"]).Count);
        var history = await Object("QueryAuditPresentationChanges", new JsonObject { ["auditEventId"] = id });
        Assert.Equal(2, Assert.IsType<JsonArray>(history["items"]).Count);
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM AuditEvents WHERE AuditEventId=$id;", id));
        Assert.True(_db.HasValidIntegrity());
    }

    [Fact]
    public async Task PersonnelIdentity_RealPreviewEdit_PreservesCanonicalNumberAndHistoricalIdentity()
    {
        // Trusted setup only: earlier history makes today's identity transition valid.
        var seeded = await new PersonRosterService(_db).CreatePersonAsync("USR-SYSTEM", "fixture", new JsonObject {
            ["visibleName"] = "هوية اختبارية سابقة", ["serviceNumberA"] = "008811",
            ["firstRecordedLocalDate"] = DateTime.Today.AddDays(-2).ToString("yyyy-MM-dd")
        }, default);
        Assert.True(seeded.Success, seeded.UserMessage);
        string id = Assert.IsType<JsonObject>(seeded.Data)["personId"]!.GetValue<string>();
        var intent = new JsonObject {
            ["personId"] = id, ["visibleName"] = "هوية اختبارية محدثة", ["serviceNumberA"] = "۰۰۸۸۱۲",
            ["baseRevision"] = 1, ["expectedRevision"] = 1,
            ["effectiveLocalDate"] = DateTime.Today.ToString("yyyy-MM-dd"), ["reason"] = "تصحيح هوية اختبارية"
        };
        long before = AuditCount();
        var preview = await Object("PreviewPersonIdentityUpdate", intent);
        Assert.Equal("008812", preview["newServiceNumberA"]!.GetValue<string>());
        Assert.Equal(before, AuditCount());
        Assert.Equal("008811", Scalar("SELECT CurrentServiceNumberA FROM Persons WHERE PersonId=$id;", id));
        var saved = await Object("UpdatePersonIdentity", intent);
        await ReplayWithoutNewAudit("UpdatePersonIdentity", intent, saved);
        Assert.Equal("008812", Scalar("SELECT CurrentServiceNumberA FROM Persons WHERE PersonId=$id;", id));
        Assert.Equal("008811", Scalar("SELECT ServiceNumberASnapshot FROM PersonIdentityHistory WHERE PersonId=$id AND ValidToLocalDate IS NOT NULL;", id));
        Assert.Equal("008812", Scalar("SELECT ServiceNumberASnapshot FROM PersonIdentityHistory WHERE PersonId=$id AND ValidToLocalDate IS NULL;", id));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM Persons WHERE PersonId=$id;", id));
        Assert.Equal(before + 1, AuditCount());
        var stale = (JsonObject)intent.DeepClone();
        stale["operationId"] = Guid.NewGuid().ToString("N");
        Assert.False((await Dispatch("UpdatePersonIdentity", stale)).Success);
        Assert.Equal(before + 1, AuditCount());
        var current = await Object("GetPersonSummary", new JsonObject { ["personId"] = id });
        Assert.Equal(id, current["personId"]!.GetValue<string>());
        Assert.Equal("008812", current["serviceNumberA"]!.GetValue<string>());
        Assert.True(_db.HasValidIntegrity());
    }

    [Fact]
    public async Task PersonnelArchive_RealPreviewCommit_HidesRosterWithoutErasingIdentity()
    {
        using (var connection = _db.CreateOpenConnection())
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
                    NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,
                    OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES('R01-ARCHIVE','فرد أرشفة اختباري','فرد ارشفه اختباري','091871','091871',
                    '2026-01-01','2026-01-01T00:00:00Z',1,1,1);
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,CreatedAtUtc)
                VALUES('R01-ARCHIVE-ASSIGN','R01-ARCHIVE','SITE-UNSPECIFIED','2026-01-01','2026-01-01T00:00:00Z');
                """;
            await seed.ExecuteNonQueryAsync();
        }
        var query = new JsonObject { ["term"] = "091871", ["pageSize"] = 1 };
        var before = await Object("QueryPersons", query);
        Assert.Equal("R01-ARCHIVE", before["items"]![0]!["personId"]!.GetValue<string>());
        var summary = await Object("GetPersonSummary", new JsonObject { ["personId"] = "R01-ARCHIVE" });
        Assert.True(summary["active"]!.GetValue<bool>());
        string day = DateTime.Today.ToString("yyyy-MM-dd");
        var previewIntent = new JsonObject { ["personIds"] = new JsonArray("R01-ARCHIVE"), ["effectiveLocalDate"] = day };
        long auditBefore = AuditCount();
        var preview = await Object("PreviewArchivePersons", previewIntent);
        Assert.Equal(auditBefore, AuditCount());
        Assert.Equal("1", Scalar("SELECT ActiveFlag FROM Persons WHERE PersonId='R01-ARCHIVE';"));
        var intent = new JsonObject {
            ["personIds"] = new JsonArray("R01-ARCHIVE"), ["effectiveLocalDate"] = day,
            ["baseRevision"] = preview["baseRevision"]!.DeepClone(),
            ["expectedRevision"] = preview["baseRevision"]!.DeepClone(),
            ["previewToken"] = preview["previewToken"]!.DeepClone(), ["reason"] = "أرشفة اختبارية مع حفظ التاريخ"
        };
        var archived = await Object("ArchivePersons", intent);
        await ReplayWithoutNewAudit("ArchivePersons", intent, archived);
        long committedAudit = AuditCount();
        var conflicting = (JsonObject)intent.DeepClone();
        conflicting["reason"] = "سبب مختلف لنفس العملية";
        Assert.False((await Dispatch("ArchivePersons", conflicting)).Success);
        var stale = (JsonObject)intent.DeepClone();
        stale["operationId"] = Guid.NewGuid().ToString("N");
        Assert.False((await Dispatch("ArchivePersons", stale)).Success);
        Assert.Equal(committedAudit, AuditCount());
        Assert.Equal("4", Scalar("SELECT OrganizationalStatus FROM Persons WHERE PersonId='R01-ARCHIVE';"));
        Assert.Equal("0", Scalar("SELECT ActiveFlag FROM Persons WHERE PersonId='R01-ARCHIVE';"));
        Assert.Equal(day, Scalar("SELECT ValidToLocalDate FROM PersonAssignments WHERE PersonId='R01-ARCHIVE';"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='PERSON_ARCHIVE';"));
        Assert.Empty(Assert.IsType<JsonArray>((await Object("QueryPersons", query))["items"]));
        query["includeArchived"] = true;
        Assert.Equal("R01-ARCHIVE", (await Object("QueryPersons", query))["items"]![0]!["personId"]!.GetValue<string>());
        var after = await Object("GetPersonSummary", new JsonObject { ["personId"] = "R01-ARCHIVE" });
        Assert.False(after["active"]!.GetValue<bool>());
        Assert.Equal(summary["firstRecordedLocalDate"]!.GetValue<string>(), after["firstRecordedLocalDate"]!.GetValue<string>());
        Assert.True(_db.HasValidIntegrity());
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
