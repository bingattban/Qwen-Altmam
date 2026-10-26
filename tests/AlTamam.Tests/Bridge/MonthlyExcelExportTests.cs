using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Exports are genuine offline .xlsx ZIP packages, safe against Excel formulas and file clobbering.</summary>
public sealed class MonthlyExcelExportTests : IAsyncLifetime, IDisposable
{
    private readonly string _file = SyntheticFixtures.CreateTempDatabasePath();
    private readonly List<string> _exports = new();
    private readonly string _output = Path.Combine(Path.GetTempPath(),"altamam-monthly-output-"+Guid.NewGuid().ToString("N"));
    private AlTamamDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_file, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        Directory.CreateDirectory(_output);
        using var connection = _db.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Persons (PersonId, CurrentVisibleName, NormalizedName,
                CurrentServiceNumberA, NormalizedServiceNumberA, FirstRecordedLocalDate,
                FirstRecordedAtUtc, OrganizationalStatus, ActiveFlag, RowVersion)
            VALUES ('TST-EX-0','=HYPERLINK("https://example.invalid")',
                'اسم اختبار','EX000','EX000','2026-10-01','2026-10-01T07:00:00Z',1,1,1);
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-EX-0','=HYPERLINK("https://example.invalid")','EX000',
                '2026-10-01',NULL,'TST-U','2026-10-01T07:00:00Z','سجل تجريبي');
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES('TST-EX-A0','TST-EX-0','SITE-UNSPECIFIED',
                '2026-10-01',NULL,NULL,'2026-10-01T07:00:00Z');
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                Source,CreatedByUserId,CommittedAtUtc,IdempotencyKey)
            VALUES('TST-EX-0','2026-10-19','STS-ABSENT','غياب','غ','#EF4444',
                'TST-EX-A0','SITE-UNSPECIFIED','غير محدد','manual','TST-U',
                '2026-10-19T07:00:00Z','TST-EX-ATT');
            """;
        await command.ExecuteNonQueryAsync();
        command.CommandText="UPDATE AppSettings SET SettingValue=$folder WHERE SettingKey='PreferredBackupDirectory';";
        command.Parameters.AddWithValue("$folder",_output);
        await command.ExecuteNonQueryAsync();
        // 101 synthetic entries exercise the real 100-row SQLCipher paging boundary.
        for (int i = 1; i <= 101; i++)
        {
            using var c = connection.CreateCommand();
            c.CommandText = """
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                    CurrentServiceNumberA,NormalizedServiceNumberA,
                    FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES($id,$name,$name,$number,$number,'2026-10-01','2026-10-01T07:00:00Z',1,1,1);
                INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                    ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
                VALUES($id,$name,$number,'2026-10-01',NULL,'TST-U',
                    '2026-10-01T07:00:00Z','سجل تجريبي');
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                    ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES($assignment,$id,'SITE-UNSPECIFIED','2026-10-01',NULL,NULL,'2026-10-01T07:00:00Z');
                """;
            c.Parameters.AddWithValue("$id", $"TST-EX-{i}");
            c.Parameters.AddWithValue("$name", $"فرد {i}");
            c.Parameters.AddWithValue("$number", $"EX{i:000}");
            c.Parameters.AddWithValue("$assignment", $"TST-EX-A{i}");
            await c.ExecuteNonQueryAsync();
        }
    }

    private static JsonObject Period(string site = "") => new()
    {
        ["year"] = 2026, ["month"] = 10, ["siteId"] = site
    };

    [Fact]
    public async Task ExcelPreview_HasRealTotalsAndRejectsInvalidOrEmptyPeriods()
    {
        var excel = new MonthlyExcelExportService(_db);
        var preview = await excel.PreviewAsync(Period("SITE-UNSPECIFIED"), CancellationToken.None);
        Assert.True(preview.Success, preview.UserMessage);
        Assert.Equal(102L, Assert.IsType<JsonObject>(preview.Data)["personCount"]!.GetValue<long>());
        var invalid = await excel.PreviewAsync(new JsonObject { ["year"] = 0, ["month"] = 13 },
            CancellationToken.None);
        Assert.False(invalid.Success);
        var empty = await excel.ExportAsync(Period("SITE-NOT-FOUND"), CancellationToken.None);
        Assert.False(empty.Success);
        var months = await excel.PeriodsAsync(CancellationToken.None);
        Assert.Contains(Assert.IsType<JsonArray>(months.Data), x => x!.GetValue<string>() == "2026-10");
    }

    [Fact]
    public async Task ExportPeriodDiscovery_IncludesEnrollmentWithoutAttendance()
    {
        // A newly admitted workforce has a valid blank monthly report even
        // before attendance is committed, including its site/name snapshots.
        var svc=new MonthlyExcelExportService(_db);
        using(var connection=_db.CreateOpenConnection())
        using(var cmd=connection.CreateCommand())
        {
            cmd.CommandText="""
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                    CurrentServiceNumberA,NormalizedServiceNumberA,
                    FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES('TST-EXPORT-NO-ATT','فرد مقيد فقط','فرد مقيد فقط','77221','77221',
                    '2026-06-01','2026-06-01T08:00:00Z',4,0,1);
                INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                    ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
                VALUES('TST-EXPORT-NO-ATT','فرد مقيد فقط','77221',
                    '2026-06-01',NULL,'TST-U','2026-06-01T08:00:00Z','قيد بدون تحضير');
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,
                    ValidFromLocalDate,ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES('TST-EXPORT-NO-ATT-A','TST-EXPORT-NO-ATT','SITE-UNSPECIFIED',
                    '2026-06-01','2026-08-01',NULL,'2026-06-01T08:00:00Z');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var periods=await svc.PeriodsAsync(CancellationToken.None);
        var options=Assert.IsType<JsonArray>(periods.Data)
            .Select(x=>x!.GetValue<string>()).ToArray();
        Assert.Contains("2026-06",options);
        Assert.Contains("2026-07",options);
        var preview=await svc.PreviewAsync(new JsonObject{
            ["year"]=2026,["month"]=7,["siteId"]="SITE-UNSPECIFIED"
        },CancellationToken.None);
        Assert.True(preview.Success,preview.UserMessage);
        Assert.Equal(1L,((JsonObject)preview.Data!)["personCount"]!.GetValue<long>());
        var grid=await new DailyMonthlyReadService(_db).MonthlyGridAsync(new JsonObject{
            ["year"]=2026,["month"]=7,["siteId"]="SITE-UNSPECIFIED"
        },CancellationToken.None);
        var item=Assert.Single(Assert.IsType<JsonArray>(((JsonObject)grid.Data!)["items"]));
        Assert.Equal("فرد مقيد فقط",item!["name"]!.GetValue<string>());
        Assert.Equal("غير محدد",item["siteName"]!.GetValue<string>());
        Assert.Empty(Assert.IsType<JsonObject>(item["days"]));
    }

    [Fact]
    public async Task FilteredHistoricalExcel_EqualsMonthlyGrid_IncludingReusedNumberAcrossPersonIds()
    {
        // A dismissed individual and an active different human have identical
        // numbers. Historical attendance belongs ONLY to the archived PersonId.
        using(var connection=_db.CreateOpenConnection())
        using(var cmd=connection.CreateCommand())
        {
            cmd.CommandText="""
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                    CurrentServiceNumberA,NormalizedServiceNumberA,
                    FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES('TST-EX-ARCHIVED','فرد مفصول مستقل','فرد مفصول مستقل',
                    'EX001','EX001','2026-09-01','2026-09-01T07:00:00Z',3,0,2);
                INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                    ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
                VALUES('TST-EX-ARCHIVED','فرد مفصول مستقل','EX001',
                    '2026-09-01',NULL,'TST-U','2026-09-01T07:00:00Z','فصل مع أرشفة');
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                    ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES('TST-EX-OLD-ASG','TST-EX-ARCHIVED','SITE-UNSPECIFIED',
                    '2026-09-01','2026-10-20',NULL,'2026-09-01T07:00:00Z');
                INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                    StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                    AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                    Source,CreatedByUserId,CommittedAtUtc,IdempotencyKey)
                VALUES('TST-EX-ARCHIVED','2026-10-15','STS-PRESENT','موجود','م','#16A34A',
                    'TST-EX-OLD-ASG','SITE-UNSPECIFIED','غير محدد','manual','TST-U',
                    '2026-10-15T07:00:00Z','TST-EX-ARCHIVED-ATT');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var selected=new JsonObject{
            ["year"]=2026,["month"]=10,["siteId"]="SITE-UNSPECIFIED",
            ["term"]="EX٠٠١"
        };
        var month=new DailyMonthlyReadService(_db);
        var response=await month.MonthlyGridAsync((JsonObject)selected.DeepClone(),
            CancellationToken.None);
        Assert.True(response.Success,response.UserMessage);
        var grid=Assert.IsType<JsonObject>(response.Data);
        Assert.Equal(2L,grid["total"]!.GetValue<long>());
        var people=Assert.IsType<JsonArray>(grid["items"]).OfType<JsonObject>().ToArray();
        Assert.Equal(2,people.Length);
        Assert.All(people,p=>Assert.Equal("EX001",p["serviceNumberA"]!.GetValue<string>()));
        Assert.Single(people.Where(p=>p["personId"]!.GetValue<string>()=="TST-EX-ARCHIVED"));
        Assert.Single(people.Where(p=>p["personId"]!.GetValue<string>()=="TST-EX-1"));

        var exporter=new MonthlyExcelExportService(_db);
        var preview=await exporter.PreviewAsync((JsonObject)selected.DeepClone(),CancellationToken.None);
        Assert.True(preview.Success,preview.UserMessage);
        Assert.Equal(grid["total"]!.GetValue<long>(),
            ((JsonObject)preview.Data!)["personCount"]!.GetValue<long>());
        Assert.Equal("EX٠٠١",((JsonObject)preview.Data!)["term"]!.GetValue<string>());
        var export=await exporter.ExportAsync((JsonObject)selected.DeepClone(),CancellationToken.None);
        Assert.True(export.Success,export.UserMessage);
        var meta=Assert.IsType<JsonObject>(export.Data);
        Assert.Equal(2L,meta["personCount"]!.GetValue<long>());
        Assert.Equal("EX٠٠١",meta["term"]!.GetValue<string>());
        string location=meta["filePath"]!.GetValue<string>();
        _exports.Add(location);
        using(var package=ZipFile.OpenRead(location))
        using(var part=package.GetEntry("xl/worksheets/sheet1.xml")!.Open())
        {
            var xml=XDocument.Load(part);
            XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var rows=xml.Descendants(ns+"row").ToArray();
            Assert.Equal(5,rows.Length);
            var header=rows[1].Descendants(ns+"t").Select(x=>x.Value).ToArray();
            Assert.Contains("EX٠٠١",header);  // filter evidence in Excel itself
            for(int i=0;i<people.Length;i++)
            {
                var row=rows[i+3].Descendants(ns+"t").Select(x=>x.Value).ToArray();
                Assert.Equal(i+1+"",row[0]);
                Assert.Equal(people[i]["serviceNumberA"]!.GetValue<string>(),row[1]);
                Assert.Equal(people[i]["name"]!.GetValue<string>(),row[2]);
                Assert.Equal(people[i]["siteName"]!.GetValue<string>(),row[3]);
                var days=Assert.IsType<JsonObject>(people[i]["days"]);
                for(int day=1;day<=31;day++)
                {
                    string date="2026-10-"+day.ToString("D2");
                    string expected=(days[date] as JsonObject)?["name"]?.GetValue<string>()??"";
                    Assert.Equal(expected,row[day+3]);
                }
            }
            Assert.Empty(xml.Descendants(ns+"f")); // no formula injection
        }
        var invalid=await exporter.PreviewAsync(new JsonObject{
            ["year"]=2026,["month"]=10,["term"]=new string('a',101)
        },CancellationToken.None);
        Assert.False(invalid.Success);
        var noMatch=await exporter.PreviewAsync(new JsonObject{
            ["year"]=2026,["month"]=10,["term"]="%"
        },CancellationToken.None);
        Assert.Equal(0L,Assert.IsType<JsonObject>(noMatch.Data)["personCount"]!.GetValue<long>());
        var rejected=await exporter.ExportAsync(new JsonObject{
            ["year"]=2026,["month"]=10,["term"]="%"
        },CancellationToken.None);
        Assert.False(rejected.Success);
    }

    [Fact]
    public async Task ExcelArchive_IsReadableNativeXlsx_AllRowsEscapedAndHistoricalDaysRetained()
    {
        var excel = new MonthlyExcelExportService(_db);
        var result = await excel.ExportAsync(Period(), CancellationToken.None);
        Assert.True(result.Success, result.UserMessage);
        var metadata = Assert.IsType<JsonObject>(result.Data);
        var name = metadata["filePath"]!.GetValue<string>();
        _exports.Add(name);
        Assert.Equal("xlsx", metadata["format"]!.GetValue<string>());
        Assert.EndsWith(".xlsx", name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(102L, metadata["personCount"]!.GetValue<long>());
        using (var archive = ZipFile.OpenRead(name))
        {
            foreach (var path in new[] {
                "[Content_Types].xml", "_rels/.rels",
                "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/worksheets/sheet1.xml"
            }) Assert.NotNull(archive.GetEntry(path));
            using var data = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var xml = XDocument.Load(data);
            XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var rows = xml.Descendants(main + "row").ToArray();
            Assert.Equal(105, rows.Length); // 102 persons and three informational/header rows
            var values = xml.Descendants(main + "t").Select(t => t.Value).ToArray();
            Assert.Contains("غياب", values);
            Assert.Contains("EX000", values);
            Assert.Contains("EX101", values);
            Assert.Contains(@"=HYPERLINK(""https://example.invalid"")", values);
            Assert.Empty(xml.Descendants(main + "f")); // formula injection prohibited
            Assert.Equal("1", xml.Descendants(main + "sheetView").Single()
                .Attribute("rightToLeft")?.Value);
        }
        var again = await excel.ExportAsync(Period(), CancellationToken.None);
        Assert.True(again.Success, again.UserMessage);
        string second = ((JsonObject)again.Data!)["filePath"]!.GetValue<string>();
        _exports.Add(second);
        Assert.NotEqual(name, second);
        Assert.True(File.Exists(name));
        Assert.True(File.Exists(second));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _db?.Dispose();
        foreach (var path in _exports)
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        SyntheticFixtures.DeleteTempDatabase(_file);
        try{Directory.Delete(_output,true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
