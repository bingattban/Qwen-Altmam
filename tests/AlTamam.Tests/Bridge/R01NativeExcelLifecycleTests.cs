using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace AlTamam.Tests.Bridge;

// Real authenticated dispatch -> encrypted SQL -> real OOXML bytes.
// Direct SQL only creates independent historical synthetic fixture facts.
public sealed class R01NativeExcelLifecycleTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "AlTamam-R01-Xlsx-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private AlTamamDbContext _db = null!;
    private BridgeSharedServices _services = null!;
    private WebView2BridgeDispatcher _dispatcher = null!;
    private readonly SyntheticDaytimeClock _clock = new();
    private string _nonce = new('0', 32);
    private const string SiteB = "R01-XLSX-SITE-B";
    private static string Id(int i) => "R01-XLSX-P" + i.ToString("D3");
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public R01NativeExcelLifecycleTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex, useConnectionPooling: false);
        await _db.InitializeAsync();
        _services = new BridgeSharedServices(_db, _clock);
        _dispatcher = BridgeHandlerCatalog.Create(_services);
        var login = await Object("AuthenticateLocalUser", new JsonObject {
            ["username"] = "r01-excel-owner", ["password"] = "Native123",
            ["passwordConfirmation"] = "Native123", ["setupIntent"] = "firstTimeSetup"
        });
        _nonce = login["sessionNonce"]!.GetValue<string>();
        Assert.NotNull(await _services.ResolveSessionAsync(_nonce));
        await Object("SetOutputDirectory", new JsonObject { ["directory"] = _folder });
        using var connection = _db.CreateOpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var site = connection.CreateCommand())
        {
            site.Transaction = transaction;
            site.CommandText = """
                INSERT INTO Sites(SiteId,VisibleName,NormalizedName,IsSystemSite,ActiveFlag,CreatedAtUtc,RowVersion)
                VALUES('R01-XLSX-SITE-B','موقع التصدير ب','موقع التصدير ب',0,1,'2026-10-01T00:00:00Z',1);
                INSERT INTO SiteNameHistory(SiteId,VisibleNameSnapshot,ValidFromLocalDate,ChangedByUserId,RecordedAtUtc)
                VALUES('R01-XLSX-SITE-B','موقع التصدير ب','2026-10-01','USR-SYSTEM','2026-10-01T00:00:00Z');
                """;
            await site.ExecuteNonQueryAsync();
        }
        for (int i = 0; i < 105; i++)
        {
            using var seed = connection.CreateCommand(); seed.Transaction = transaction;
            seed.CommandText = """
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,CurrentServiceNumberA,
                    NormalizedServiceNumberA,FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES($id,$name,$name,$number,$number,$start,$recorded,1,1,1);
                INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,ValidFromLocalDate,
                    ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
                VALUES($id,$oldName,$oldNumber,$start,NULL,'USR-SYSTEM',$recorded,'synthetic history');
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,CreatedAtUtc)
                VALUES($assignment,$id,$site,$start,$recorded);
                """;
            seed.Parameters.AddWithValue("$id", Id(i));
            seed.Parameters.AddWithValue("$assignment", "R01-XLSX-A" + i);
            seed.Parameters.AddWithValue("$name", "فرد " + i.ToString("D3"));
            seed.Parameters.AddWithValue("$number", "009" + i.ToString("D3"));
            seed.Parameters.AddWithValue("$oldName", i == 0 ? "الاسم التاريخي المحفوظ" : "فرد " + i.ToString("D3"));
            seed.Parameters.AddWithValue("$oldNumber", i == 0 ? "000123" : "009" + i.ToString("D3"));
            seed.Parameters.AddWithValue("$site", i % 2 == 0 ? "SITE-UNSPECIFIED" : SiteB);
            string start = i == 0 ? "2026-01-01" : "2026-10-01";
            seed.Parameters.AddWithValue("$start", start);
            seed.Parameters.AddWithValue("$recorded", start + "T00:00:00Z");
            await seed.ExecuteNonQueryAsync();
        }
        using (var facts = connection.CreateCommand())
        {
            facts.Transaction = transaction;
            facts.CommandText = """
                UPDATE PersonIdentityHistory SET ValidToLocalDate='2026-10-21' WHERE PersonId='R01-XLSX-P000';
                INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,ValidFromLocalDate,
                    ChangedByUserId,RecordedAtUtc,Reason)
                VALUES('R01-XLSX-P000','فرد 000','009000','2026-10-21','USR-SYSTEM','2026-10-21T00:00:00Z','synthetic rename');
                INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,StatusNameSnapshot,
                    StatusSymbolSnapshot,StatusColorSnapshot,AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,
                    SiteNameSnapshot,Source,CreatedByUserId,CommittedAtUtc,IdempotencyKey)
                VALUES('R01-XLSX-P000','2026-10-19','STS-ABSENT','غياب','غ','#EF4444','R01-XLSX-A0',
                    'SITE-UNSPECIFIED','غير محدد','manual','USR-SYSTEM','2026-10-19T08:00:00Z','R01-XLSX-ATT-0'),
                    ('R01-XLSX-P000','2026-10-20','STS-PRESENT','موجود','م','#22C55E','R01-XLSX-A0',
                    'SITE-UNSPECIFIED','غير محدد','manual','USR-SYSTEM','2026-10-20T08:00:00Z','R01-XLSX-ATT-1'),
                    ('R01-XLSX-P001','2026-10-19','STS-ABSENT','غياب','غ','#EF4444','R01-XLSX-A1',
                    'R01-XLSX-SITE-B','موقع التصدير ب','manual','USR-SYSTEM','2026-10-19T08:00:00Z','R01-XLSX-ATT-2');
                """;
            await facts.ExecuteNonQueryAsync();
        }
        // Cross the canonical report's real fixed 100-row page boundary.
        for (int i = 0; i < 101; i++)
        {
            using var olderDay = connection.CreateCommand(); olderDay.Transaction = transaction;
            olderDay.CommandText = """
                INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,StatusNameSnapshot,
                    StatusSymbolSnapshot,StatusColorSnapshot,AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,
                    SiteNameSnapshot,Source,CreatedByUserId,CommittedAtUtc,IdempotencyKey)
                VALUES('R01-XLSX-P000',$day,'STS-PRESENT','موجود','م','#22C55E','R01-XLSX-A0',
                    'SITE-UNSPECIFIED','غير محدد','manual','USR-SYSTEM',$now,$key);
                """;
            string day = new DateOnly(2026, 6, 1).AddDays(i).ToString("yyyy-MM-dd");
            olderDay.Parameters.AddWithValue("$day", day);
            olderDay.Parameters.AddWithValue("$now", day + "T08:00:00Z");
            olderDay.Parameters.AddWithValue("$key", "R01-XLSX-OLDER-" + i);
            await olderDay.ExecuteNonQueryAsync();
        }
        transaction.Commit();
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
    private string Scalar(string sql)
    {
        using var connection = _db.CreateOpenConnection(); using var command = connection.CreateCommand();
        command.CommandText = sql; return Convert.ToString(command.ExecuteScalar()) ?? "";
    }
    private static JsonObject Period(string site = "") => new() {
        ["year"] = 2026, ["month"] = 10, ["siteId"] = site, ["archiveMode"] = true
    };
    private static string Text(JsonNode? node) => node?.ToString() ?? "";
    private static List<string[]> Sheet(string path, string name)
    {
        Assert.True(File.Exists(path)); Assert.False(File.Exists(path + ".part"));
        using var zip = ZipFile.OpenRead(path);
        using var workbook = zip.GetEntry("xl/workbook.xml")!.Open();
        var sheets = XDocument.Load(workbook).Descendants(Ns + "sheet").ToArray();
        int index = Array.FindIndex(sheets, s => s.Attribute("name")?.Value == name);
        Assert.True(index >= 0, "Missing sheet " + name);
        using var stream = zip.GetEntry("xl/worksheets/sheet" + (index + 1) + ".xml")!.Open();
        var xml = XDocument.Load(stream);
        Assert.Empty(xml.Descendants(Ns + "f"));
        Assert.Equal("1", xml.Descendants(Ns + "sheetView").Single().Attribute("rightToLeft")?.Value);
        var formats = new Dictionary<string, string>();
        var styleFormats = new List<string>();
        if (zip.GetEntry("xl/styles.xml") is { } stylesEntry)
        {
            using var stylesStream = stylesEntry.Open();
            var styles = XDocument.Load(stylesStream);
            foreach (var format in styles.Descendants(Ns + "numFmt"))
                formats.Add(format.Attribute("numFmtId")!.Value, format.Attribute("formatCode")!.Value);
            styleFormats.AddRange(styles.Root!.Element(Ns + "cellXfs")!.Elements(Ns + "xf")
                .Select(x => x.Attribute("numFmtId")!.Value));
        }
        string Value(XElement cell)
        {
            if (cell.Element(Ns + "is") is { } text) return text.Element(Ns + "t")?.Value ?? "";
            string value = cell.Element(Ns + "v")?.Value ?? "";
            int style = int.Parse(cell.Attribute("s")?.Value ?? "0");
            if (value.Length == 0 || styleFormats.Count == 0 || styleFormats[style] == "0") return value;
            string format = formats[styleFormats[style]];
            Assert.Contains(format, new[] { "yyyy-mm-dd", "yyyy-mm-dd hh:mm" });
            var instant = DateTime.FromOADate(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
            return instant.ToString(format == "yyyy-mm-dd" ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss'Z'",
                System.Globalization.CultureInfo.InvariantCulture);
        }
        return xml.Descendants(Ns + "row").Select(r => r.Elements(Ns + "c").Select(Value).ToArray()).ToList();
    }

    [Fact]
    public async Task Monthly_RealAllSitesAndFilteredWorkbooks_ExactlyMatchEveryPagedNativeCell()
    {
        var periods = Assert.IsType<JsonArray>((await Call("GetExportablePeriods")).Data);
        Assert.Contains(periods, p => p!.GetValue<string>() == "2026-10");
        var preview = await Object("PreviewMonthlyExport", Period());
        Assert.Equal(105L, preview["personCount"]!.GetValue<long>());
        var exported = await Object("ExportMonthlyWorkbookSet", Period());
        var files = Assert.IsType<JsonArray>(exported["files"]);
        Assert.Equal(3, files.Count); // Consolidated, unspecified, second site.
        foreach (var file in files)
        {
            string site = Text(file!["siteId"]), path = Text(file["filePath"]);
            var all = new List<JsonObject>();
            for (int page = 1; ; page++)
            {
                var query = Period(site); query["page"] = page; query["pageSize"] = 100;
                var data = await Object("QueryMonthlyGrid", query);
                all.AddRange(Assert.IsType<JsonArray>(data["items"]).OfType<JsonObject>());
                if (!data["hasNext"]!.GetValue<bool>()) break;
            }
            Assert.Equal(site == "" ? 105 : site == SiteB ? 52 : 53, all.Count);
            Assert.Equal(all.Count, all.Select(x => Text(x["personId"])).Distinct().Count());
            Assert.Equal(site == "" ? 3 : site == SiteB ? 1 : 2, all.Sum(x => Assert.IsType<JsonObject>(x["days"]).Count));
            if (site != SiteB)
            {
                var known = Assert.Single(all, x => Text(x["personId"]) == Id(0));
                Assert.Equal("009000", Text(known["serviceNumberA"]));
                Assert.Equal("غياب", Text(known["days"]!["2026-10-19"]!["name"]));
                Assert.Equal("موجود", Text(known["days"]!["2026-10-20"]!["name"]));
                Assert.Equal("000123", Text(known["days"]!["2026-10-19"]!["serviceNumberA"]));
                Assert.Equal("الاسم التاريخي المحفوظ", Text(known["days"]!["2026-10-19"]!["personName"]));
            }
            var rows = Sheet(path, "التمام الشهري");
            Assert.Equal(new[] { "م", "الرقم (ع)", "الاسم", "الموقع" }, rows[2].Take(4));
            Assert.Equal(all.Count + 3, rows.Count);
            for (int i = 0; i < all.Count; i++)
            {
                var expected = new List<string> { (i + 1).ToString(), Text(all[i]["serviceNumberA"]), Text(all[i]["name"]), Text(all[i]["siteName"]) };
                for (int d = 1; d <= 31; d++) expected.Add(Text(all[i]["days"]![$"2026-10-{d:D2}"]?["name"]));
                Assert.Equal(expected, rows[i + 3]);
            }
        }
        var filtered = Period("SITE-UNSPECIFIED"); filtered["term"] = "فرد 000";
        var one = await Object("ExportMonthlyWorkbookSet", filtered);
        Assert.Equal(4, Sheet(Text(one["filePath"]), "التمام الشهري").Count);
        var progress = await Object("GetExportProgress");
        Assert.True(progress["completed"]!.GetValue<bool>());
        Assert.Equal(100, progress["percentage"]!.GetValue<int>());
        Assert.Equal(Text(one["filePath"]), Text(progress["filePath"]));
        Assert.False((await Dispatch("CancelExport")).Success); // A committed XLSX cannot be canceled.
        Assert.True(File.Exists(Text(one["filePath"])));
        Assert.Equal("104", Scalar("SELECT COUNT(*) FROM AttendanceRecords;"));
        Assert.True(_db.HasValidIntegrity());
    }

    [Fact]
    public async Task PersonReports_RealSingleGroupFilteredAndPagedExcel_PreserveHistoricalIdentityAndTotals()
    {
        var request = new JsonObject {
            ["personId"] = Id(0), ["dateFrom"] = "2026-06-01", ["dateTo"] = "2026-10-20"
        };
        var expectedDays = new List<JsonNode?>();
        for (int page = 1; page <= 2; page++)
        {
            request["attendancePage"] = page;
            var canonical = await Object("BuildPersonLifetimeReport", request);
            Assert.Equal(103L, canonical["attendanceTotal"]!.GetValue<long>());
            Assert.Equal(page == 1, canonical["attendanceHasNext"]!.GetValue<bool>());
            Assert.Equal(page == 1 ? 100 : 3, Assert.IsType<JsonArray>(canonical["attendance"]).Count);
            expectedDays.AddRange(Assert.IsType<JsonArray>(canonical["attendance"]));
        }
        Assert.Equal(103, expectedDays.Count);
        Assert.Equal(102, expectedDays.Count(d => Text(d!["statusName"]) == "موجود"));
        Assert.Single(expectedDays, d => Text(d!["statusName"]) == "غياب");
        Assert.Equal("2026-10-20", Text(expectedDays[0]!["date"]));
        Assert.Equal("2026-06-01", Text(expectedDays[^1]!["date"]));
        Assert.All(expectedDays, d => { Assert.Equal("000123", Text(d!["serviceNumberA"])); Assert.Equal("الاسم التاريخي المحفوظ", Text(d["name"])); });
        request["mode"] = "report"; request["exportId"] = Guid.NewGuid().ToString("N");
        var previewRequest = (JsonObject)request.DeepClone(); previewRequest["exportPreview"] = true;
        var preview = await Object("BuildPersonLifetimeReport", previewRequest);
        var exported = await Object("ExportPersonLifetimeExcel", request);
        Assert.Equal(Text(preview["fileName"]), Text(exported["fileName"]));
        Assert.Contains("فرد 000", Text(exported["fileName"]));
        var daily = Sheet(Text(exported["filePath"]), "التفاصيل التاريخية");
        Assert.Equal(104, daily.Count);
        Assert.Equal(expectedDays.Select(d => Text(d!["date"])).OrderBy(x => x), daily.Skip(1).Select(r => r[3]).OrderBy(x => x));
        foreach (var row in daily.Skip(1))
        {
            var day = Assert.Single(expectedDays, d => Text(d!["date"]) == row[3]);
            Assert.Equal(new[] { "1", Text(day!["serviceNumberA"]), Text(day["name"]), Text(day["date"]), Text(day["statusName"]), Text(day["site"]), Text(day["source"]), Text(day["revision"]), Text(day["committedAtUtc"]) }, row);
        }
        var summary = Sheet(Text(exported["filePath"]), "ملخص التقرير");
        Assert.Equal("009000", summary[1][1]); Assert.Equal("فرد 000", summary[1][2]); Assert.Equal("103", summary[1][5]);
        foreach (string empty in new[] { "سجل التنقلات", "تغييرات العمليات", "تصحيحات التحضير" })
            Assert.Single(Sheet(Text(exported["filePath"]), empty));
        var group = new JsonObject {
            ["mode"] = "report", ["exportId"] = Guid.NewGuid().ToString("N"), ["personIds"] = new JsonArray(Id(0), Id(1)),
            ["dateFrom"] = "2026-10-19", ["dateTo"] = "2026-10-19", ["statusId"] = "STS-ABSENT", ["pageSize"] = 1
        };
        var totals = await Object("BuildPersonLifetimeReport", group);
        Assert.Equal(2L, totals["attendanceTotal"]!.GetValue<long>());
        var calendar = Assert.Single(Assert.IsType<JsonArray>(totals["dailyStatusTotals"]));
        Assert.Equal(2L, calendar!["count"]!.GetValue<long>());
        var groupPreviewRequest = (JsonObject)group.DeepClone(); groupPreviewRequest["exportPreview"] = true;
        var groupPreview = await Object("BuildPersonLifetimeReport", groupPreviewRequest);
        var grouped = await Object("ExportPersonLifetimeExcel", group);
        var groupedDays = Sheet(Text(grouped["filePath"]), "التفاصيل التاريخية");
        Assert.Equal(3, groupedDays.Count); Assert.All(groupedDays.Skip(1), row => { Assert.Equal("2026-10-19", row[3]); Assert.Equal("غياب", row[4]); });
        var aggregate = Sheet(Text(grouped["filePath"]), "الإجمالي اليومي");
        Assert.Equal(2, aggregate.Count);
        Assert.Equal("2026-10-19", aggregate[1][0]); Assert.Equal("غياب", aggregate[1][1]); Assert.Equal("2", aggregate[1][2]);
        var groupSummary = Sheet(Text(grouped["filePath"]), "ملخص التقرير");
        Assert.Equal(4, groupSummary.Count); // Header, two individuals, explicit combined total.
        Assert.Equal("1", groupSummary[1][5]); Assert.Equal("1", groupSummary[2][5]);
        Assert.Equal("إجمالي المجموعة", groupSummary[3][0]); Assert.Equal("2", groupSummary[3][5]);
        // Prove the SAME historical workbook values under all three REAL DB roles.
        var roles = new[] { "OrdinaryAdministration", "Supervisor" };
        for (int i = 0; i < roles.Length; i++)
            await Object("CreateUserAccount", new JsonObject {
                ["username"] = "r01-excel-role-" + i, ["password"] = "Native123",
                ["passwordConfirmation"] = "Native123", ["role"] = roles[i], ["personId"] = Id(i + 2)
            });
        for (int i = 0; i < roles.Length; i++)
        {
            await Object("Logout");
            var login = await Object("AuthenticateLocalUser", new JsonObject {
                ["username"] = "r01-excel-role-" + i, ["password"] = "Native123"
            });
            _nonce = login["sessionNonce"]!.GetValue<string>();
            Assert.Equal(roles[i], Text(login["role"]));
            var roleRequest = (JsonObject)request.DeepClone();
            roleRequest["exportId"] = Guid.NewGuid().ToString("N");
            roleRequest["operationId"] = Guid.NewGuid().ToString("N");
            var roleExport = await Object("ExportPersonLifetimeExcel", roleRequest);
            foreach (var sheet in Assert.IsType<JsonArray>(preview["worksheets"]))
            {
                var baseline = Sheet(Text(exported["filePath"]), Text(sheet));
                var actual = Sheet(Text(roleExport["filePath"]), Text(sheet));
                Assert.Equal(baseline.Count, actual.Count);
                for (int row = 0; row < baseline.Count; row++) Assert.Equal(baseline[row], actual[row]);
            }
            var roleGroup = (JsonObject)group.DeepClone();
            roleGroup["exportId"] = Guid.NewGuid().ToString("N");
            roleGroup["operationId"] = Guid.NewGuid().ToString("N");
            var roleGroupedExport = await Object("ExportPersonLifetimeExcel", roleGroup);
            foreach (var sheet in Assert.IsType<JsonArray>(groupPreview["worksheets"]))
            {
                var baseline = Sheet(Text(grouped["filePath"]), Text(sheet));
                var actual = Sheet(Text(roleGroupedExport["filePath"]), Text(sheet));
                Assert.Equal(baseline.Count, actual.Count);
                for (int row = 0; row < baseline.Count; row++) Assert.Equal(baseline[row], actual[row]);
            }
        }

        Assert.Equal("104", Scalar("SELECT COUNT(*) FROM AttendanceRecords;"));
        Assert.True(_db.HasValidIntegrity());
    }

    private async Task<JsonObject> Stage2Object(string action, JsonObject payload)
    {
        var response = await Dispatch(action, payload);
        Assert.True(response.Success, action + ": " + response.UserMessage);
        R01WireShapeSnapshot.Verify(action, response);
        _output.WriteLine("R01_NATIVE_SHAPE " + action + " " + R01WireShapeSnapshot.Shape(response));
        return Assert.IsType<JsonObject>(response.Data);
    }

    [Fact]
    public async Task FilteredSingleReport_RealNativeAndWorkbook_HaveIdenticalEmptyAndNonemptyRows()
    {
        foreach (var (site, status, expected) in new[] {
            ("SITE-UNSPECIFIED", "STS-PRESENT", 1),
            ("SITE-UNSPECIFIED", "STS-ABSENT", 1), (SiteB, "STS-ABSENT", 0) })
        {
        var filter = new JsonObject { ["personId"] = Id(0), ["dateFrom"] = "2026-10-19",
            ["dateTo"] = "2026-10-20", ["siteId"] = site, ["statusId"] = status };
        var report = await Stage2Object("BuildPersonLifetimeReport", filter);
        Assert.Equal((long)expected, report["attendanceTotal"]!.GetValue<long>());
        var exported = await Stage2Object("ExportPersonLifetimeExcel", filter);
        var rows = Sheet(Text(exported["filePath"]), "التفاصيل التاريخية");
        Assert.Equal(expected + 1, rows.Count);
        var facts = Assert.IsType<JsonArray>(report["attendance"]);
        for (int i = 0; i < expected; i++)
        {
            var fact = facts[i]!;
            Assert.Equal("000123", rows[i + 1][1]);
            Assert.Equal("الاسم التاريخي المحفوظ", rows[i + 1][2]);
            Assert.Equal(Text(fact["date"]), rows[i + 1][3]);
            Assert.Equal(Text(fact["statusName"]), rows[i + 1][4]);
            Assert.Equal(Text(fact["site"]), rows[i + 1][5]);
        }
        Assert.Equal("104", Scalar("SELECT COUNT(*) FROM AttendanceRecords;"));
        Assert.True(_db.HasValidIntegrity());
        }
    }

    [Fact]
    public async Task ArchiveModes_RealInternalExternalAndSeparationExcel_MatchNativeArchivedFacts()
    {
        string today = _clock.GetLocalNow().ToString("yyyy-MM-dd");
        foreach (bool external in new[] { false, true })
        {
            int i = external ? 1 : 0;
            var preview = await Stage2Object(external ? "PreviewExternalTransfer" : "PreviewInternalTransfer", new JsonObject {
                ["businessDate"] = today, ["sourceSiteId"] = external ? SiteB : "SITE-UNSPECIFIED",
                ["destinationSiteId"] = external ? null : SiteB,
                ["destinationExternalName"] = external ? "جهة تصدير خارجية" : null,
                ["personIds"] = new JsonArray(Id(i))
            });
            await Stage2Object(external ? "CommitExternalTransfer" : "CommitInternalTransfer",
                new JsonObject { ["previewToken"] = preview["previewToken"]!.DeepClone() });
        }
        var separation = await Stage2Object("PreviewSeparation", new JsonObject {
            ["personId"] = Id(2), ["businessDate"] = today, ["reason"] = "سبب فصل تصدير موثق"
        });
        _clock.SetLocalTime(12, 1);
        await Stage2Object("CommitSeparation", new JsonObject { ["previewToken"] = separation["previewToken"]!.DeepClone() });
        foreach (string kind in new[] { "internal", "external", "separation" })
        {
            bool separated = kind == "separation";
            var filter = new JsonObject { ["type"] = kind, ["dateFrom"] = today, ["dateTo"] = today, ["pageSize"] = 1 };
            var canonical = await Stage2Object(separated ? "QuerySeparationArchive" : "QueryMovementArchive", filter);
            Assert.Equal(1L, canonical["total"]!.GetValue<long>());
            var fact = Assert.Single(Assert.IsType<JsonArray>(canonical["items"]))!;
            filter["mode"] = separated ? "separationArchive" : "movementArchive";
            var exported = await Stage2Object("ExportPersonLifetimeExcel", filter);
            var rows = Sheet(Text(exported["filePath"]), separated ? "أرشيف الفصل" : kind == "internal" ? "نقل داخلي" : "نقل خارجي");
            Assert.Equal(2, rows.Count);
            Assert.Equal("1", rows[1][0]);
            string[] fields = separated ? ["serviceNumberA", "name", "siteName", "reason", "date", "committedAtUtc", "actorDisplay"]
                : ["serviceNumberA", "name", "type", "sourceSite", "destination", "date", "committedAtUtc"];
            for (int i = 0; i < fields.Length; i++)
                if (fields[i] == "committedAtUtc")
                    Assert.Equal(DateTimeOffset.Parse(Text(fact[fields[i]])), DateTimeOffset.Parse(rows[1][i + 1]));
                else Assert.Equal(Text(fact[fields[i]]), rows[1][i + 1]);
        }
        Assert.Equal("3", Scalar("SELECT COUNT(*) FROM PersonnelMovements;"));
        Assert.Equal("104", Scalar("SELECT COUNT(*) FROM AttendanceRecords;"));
        Assert.True(_db.HasValidIntegrity());
    }

    [Fact]
    public async Task ReportExportErrors_RealNativeFailures_LeakNoDataAndPublishNoFiles()
    {
        long audit = long.Parse(Scalar("SELECT COUNT(*) FROM AuditEvents;"));
        foreach (var payload in new[] {
            new JsonObject { ["mode"] = "unknown" },
            new JsonObject { ["mode"] = "movementArchive", ["type"] = "unknown" },
            new JsonObject { ["personIds"] = new JsonArray(Id(0), Id(0)) },
            new JsonObject { ["personId"] = Id(0), ["exportId"] = "../invalid" },
            new JsonObject { ["personId"] = Id(0), ["dateFrom"] = "2026-10-20", ["dateTo"] = "2026-10-19" }
        })
        {
            var failed = await Dispatch("ExportPersonLifetimeExcel", payload);
            Assert.False(failed.Success);
            Assert.Equal(BridgeErrorCodes.ValidationError, failed.ErrorCode);
            Assert.Null(failed.Data);
        }
        Assert.Equal(audit, long.Parse(Scalar("SELECT COUNT(*) FROM AuditEvents;")));
        await Call("Logout");
        long afterLogout = long.Parse(Scalar("SELECT COUNT(*) FROM AuditEvents;"));
        var expired = await Dispatch("BuildPersonLifetimeReport", new JsonObject { ["personId"] = Id(0) });
        Assert.Equal(BridgeErrorCodes.SessionExpired, expired.ErrorCode); Assert.Null(expired.Data);
        Assert.Empty(Directory.GetFiles(_folder, "*.xlsx", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_folder, "*.part", SearchOption.AllDirectories));
        Assert.Equal(afterLogout, long.Parse(Scalar("SELECT COUNT(*) FROM AuditEvents;")));
        Assert.Equal("104", Scalar("SELECT COUNT(*) FROM AttendanceRecords;"));
        Assert.True(_db.HasValidIntegrity());
    }

    private static JsonObject Workbook()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text) { using var stream = zip.CreateEntry(path).Open(); stream.Write(Encoding.UTF8.GetBytes(text)); }
            Add("[Content_Types].xml", """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="xml" ContentType="application/xml"/>
                <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """);
            Add("_rels/.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Add("xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <sheets><sheet name="ورقة قبول" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
            Add("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);
            Add("xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>الاسم</t></is></c><c r="B1" t="inlineStr"><is><t>الرقم (ع)</t></is></c></row>
                <row r="2"><c r="A2" t="inlineStr"><is><t>صالح محمد اختباري</t></is></c><c r="B2" t="inlineStr"><is><t>008881</t></is></c></row>
                <row r="3"><c r="A3" t="inlineStr"><is><t>=HYPERLINK("https://example.invalid")</t></is></c><c r="B3" t="inlineStr"><is><t>009000</t></is></c></row>
                </sheetData></worksheet>
                """);
        }
        return new JsonObject { ["fileName"] = "دفعة قبول R01.xlsx", ["base64"] = Convert.ToBase64String(buffer.ToArray()) };
    }

    [Fact]
    public async Task Import_RealWorkbookAnalyzePreviewCommitOutcomeAndIssues_PersistExactlyOneAcceptedPerson()
    {
        var selected = await Object("SelectImportWorkbook", Workbook());
        var token = new JsonObject { ["workbookToken"] = selected["workbookToken"]!.DeepClone() };
        var analysis = await Object("AnalyzeImport", token);
        Assert.Equal(2, analysis["totalRows"]!.GetValue<int>());
        Assert.Equal(1, analysis["newCount"]!.GetValue<int>()); Assert.Equal(1, analysis["issueCount"]!.GetValue<int>());
        Assert.Equal("105", Scalar("SELECT COUNT(*) FROM Persons;"));
        var preview = await Object("PreviewImportPlan", token);
        var intent = new JsonObject { ["previewToken"] = preview["previewToken"]!.DeepClone() };
        var saved = await Object("CommitImport", intent);
        Assert.Equal(1, saved["createdCount"]!.GetValue<int>()); Assert.Equal(1, saved["rejectedCount"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(saved, await Object("CommitImport", intent)));
        Assert.Equal("106", Scalar("SELECT COUNT(*) FROM Persons;"));
        Assert.Equal("1", Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='PERSONNEL_IMPORT';"));
        Assert.Equal("فرد 000", Scalar("SELECT CurrentVisibleName FROM Persons WHERE PersonId='R01-XLSX-P000';"));
        Assert.Equal("صالح محمد اختباري", Scalar("SELECT CurrentVisibleName FROM Persons WHERE CurrentServiceNumberA='008881';"));
        var filter = new JsonObject { ["importBatchId"] = saved["importBatchId"]!.DeepClone() };
        var outcome = await Object("QueryImportOutcome", filter);
        Assert.Single(Assert.IsType<JsonArray>(outcome["issues"]));
        var issues = await Object("ExportImportIssues", filter);
        Assert.Equal(1, issues["issueCount"]!.GetValue<int>());
        using var zip = ZipFile.OpenRead(Text(issues["filePath"]));
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var xml = XDocument.Load(stream);
        Assert.Empty(xml.Descendants(Ns + "f"));
        Assert.Contains(xml.Descendants(Ns + "t"), t => t.Value == "009000");
        Assert.Contains(xml.Descendants(Ns + "t"), t => t.Value == "=HYPERLINK(\"https://example.invalid\")");
        Assert.False(File.Exists(Text(issues["filePath"]) + ".part"));
        Assert.True(_db.HasValidIntegrity());
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() { _db?.Dispose(); SyntheticFixtures.DeleteTempDatabase(_path); if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
