using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Infrastructure.Excel.Reports;
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
                'اسم اختبار','91000','91000','2026-10-01','2026-10-01T07:00:00Z',1,1,1);
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-EX-0','=HYPERLINK("https://example.invalid")','91000',
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
            c.Parameters.AddWithValue("$number", $"91{i:000}");
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
                    '91001','91001','2026-09-01','2026-09-01T07:00:00Z',3,0,2);
                INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                    ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
                VALUES('TST-EX-ARCHIVED','فرد مفصول مستقل','91001',
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
            ["term"]="٩١٠٠١"
        };
        // Exports use the historical archive, including exited identities.
        selected["archiveMode"]=true;
        var month=new DailyMonthlyReadService(_db);
        var response=await month.MonthlyGridAsync((JsonObject)selected.DeepClone(),
            CancellationToken.None);
        Assert.True(response.Success,response.UserMessage);
        var grid=Assert.IsType<JsonObject>(response.Data);
        Assert.Equal(2L,grid["total"]!.GetValue<long>());
        var people=Assert.IsType<JsonArray>(grid["items"]).OfType<JsonObject>().ToArray();
        Assert.Equal(2,people.Length);
        Assert.All(people,p=>Assert.Equal("91001",p["serviceNumberA"]!.GetValue<string>()));
        Assert.Single(people,p=>p["personId"]!.GetValue<string>()=="TST-EX-ARCHIVED");
        Assert.Single(people,p=>p["personId"]!.GetValue<string>()=="TST-EX-1");

        var exporter=new MonthlyExcelExportService(_db);
        var preview=await exporter.PreviewAsync((JsonObject)selected.DeepClone(),CancellationToken.None);
        Assert.True(preview.Success,preview.UserMessage);
        Assert.Equal(grid["total"]!.GetValue<long>(),
            ((JsonObject)preview.Data!)["personCount"]!.GetValue<long>());
        Assert.Equal("٩١٠٠١",((JsonObject)preview.Data!)["term"]!.GetValue<string>());
        var export=await exporter.ExportAsync((JsonObject)selected.DeepClone(),CancellationToken.None);
        Assert.True(export.Success,export.UserMessage);
        var meta=Assert.IsType<JsonObject>(export.Data);
        Assert.Equal(2L,meta["personCount"]!.GetValue<long>());
        Assert.Equal("٩١٠٠١",meta["term"]!.GetValue<string>());
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
            Assert.Contains("٩١٠٠١",header);  // filter evidence in Excel itself
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
    public async Task MonthlyExport_UsesApprovedFolderAndSafeFilename_RejectsPathInjection()
    {
        var service=new MonthlyExcelExportService(_db);
        var request=Period("SITE-UNSPECIFIED");
        request["fileName"]="كشف الشهر المعتمد.xlsx";
        request["directory"]=_output;
        var approved=await service.ExportAsync(request,CancellationToken.None);
        Assert.True(approved.Success,approved.UserMessage);
        string file=Assert.IsType<JsonObject>(approved.Data)["filePath"]!.GetValue<string>();
        _exports.Add(file);
        Assert.Equal(Path.Combine(_output,"كشف الشهر المعتمد.xlsx"),file);
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(file+".part"),"No unfinished XLSX part may remain after success.");
        var repeated=await service.ExportAsync(request,CancellationToken.None);
        Assert.False(repeated.Success); // an existing workbook is never overwritten
        Assert.True(File.Exists(file));
        foreach(string candidate in new[]{"../inside.xlsx","C:\\unsafe.xlsx",
            "CON.xlsx","report.xlsm","a?.xlsx"})
        {
            request["fileName"]=candidate;
            var result=await service.ExportAsync(request,CancellationToken.None);
            Assert.False(result.Success,"Unsafe candidate must be rejected: "+candidate);
        }
        Assert.True(File.Exists(file));
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
        var allFiles=Assert.IsType<JsonArray>(metadata["files"]);
        Assert.Equal(2,allFiles.Count); // consolidated + non-empty Unspecified site
        Assert.Equal(2,metadata["fileCount"]!.GetValue<int>());
        Assert.All(allFiles,entry=>{
            var path=Assert.IsType<JsonObject>(entry)["filePath"]!.GetValue<string>();
            Assert.True(File.Exists(path),"Every monthly workbook must exist before success.");
        });
        Assert.Equal("",allFiles[0]!["siteId"]!.GetValue<string>());
        Assert.Equal("SITE-UNSPECIFIED",allFiles[1]!["siteId"]!.GetValue<string>());
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
            Assert.Contains("91000", values);
            Assert.Contains("91101", values);
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


    [Fact]
    public void SharedXlsxPublisher_RejectsCorruptPartAndRespectsPrePublishCancellation()
    {
        var broken=Path.Combine(_output,"phase10-corrupt.xlsx.part");
        var destination=Path.Combine(_output,"phase10-corrupt.xlsx");
        File.WriteAllText(broken,"not a valid XLSX");
        try
        {
            Assert.Throws<InvalidDataException>(()=>
                ReportWorkbookCommit.VerifyFlushAndMove(broken,destination,CancellationToken.None));
            Assert.False(File.Exists(destination),"Corrupted temporary ZIP must not be published.");
        }
        finally { File.Delete(broken); }

        var good=Path.Combine(_output,"phase10-cancel.xlsx.part");
        var canceled=Path.Combine(_output,"phase10-cancel.xlsx");
        using(var zip=ZipFile.Open(good,ZipArchiveMode.Create))
        {
            zip.CreateEntry("[Content_Types].xml");
            zip.CreateEntry("xl/workbook.xml");
            zip.CreateEntry("xl/worksheets/sheet1.xml");
        }
        using var source=new CancellationTokenSource();
        source.Cancel();
        try
        {
            Assert.Throws<OperationCanceledException>(()=>
                ReportWorkbookCommit.VerifyFlushAndMove(good,canceled,source.Token));
            Assert.False(File.Exists(canceled),"Cancellation cannot publish a final workbook.");
        }
        finally { File.Delete(good); }
    }


    [Fact]
    public async Task TenThousandPersonMonthlyWorkbook_StreamsAllRowsWithNoPartialPublish()
    {
        // Insert 10,000 effective-dated people in three bounded set-based SQL
        // statements. A full workbook must stream every roster row, not just page 1.
        using(var connection=_db.CreateOpenConnection())
        using(var transaction=connection.BeginTransaction())
        using(var cmd=connection.CreateCommand())
        {
            cmd.Transaction=transaction;
            cmd.CommandText="""
                WITH RECURSIVE seq(i) AS (
                  SELECT 0 UNION ALL SELECT i+1 FROM seq WHERE i < 9999)
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                  CurrentServiceNumberA,NormalizedServiceNumberA,FirstRecordedLocalDate,
                  FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
                SELECT 'TST-10K-'||i,'فرد تصدير '||i,'فرد تصدير '||i,
                  printf('%06d',200000+i),printf('%06d',200000+i),
                  '2026-10-01','2026-10-01T07:00:00Z',1,1,1 FROM seq;

                WITH RECURSIVE seq(i) AS (
                  SELECT 0 UNION ALL SELECT i+1 FROM seq WHERE i < 9999)
                INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,
                  ServiceNumberASnapshot,ValidFromLocalDate,ValidToLocalDate,
                  ChangedByUserId,RecordedAtUtc,Reason)
                SELECT 'TST-10K-'||i,'فرد تصدير '||i,
                  printf('%06d',200000+i),'2026-10-01',NULL,
                  'TST-U','2026-10-01T07:00:00Z','تجربة عشرة آلاف فرد' FROM seq;

                WITH RECURSIVE seq(i) AS (
                  SELECT 0 UNION ALL SELECT i+1 FROM seq WHERE i < 9999)
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,
                  ValidFromLocalDate,ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                SELECT 'TST-10K-ASG-'||i,'TST-10K-'||i,'SITE-UNSPECIFIED',
                  '2026-10-01',NULL,NULL,'2026-10-01T07:00:00Z' FROM seq;
                """;
            await cmd.ExecuteNonQueryAsync();
            transaction.Commit();
        }
        var exporter=new MonthlyExcelExportService(_db);
        var request=Period("SITE-UNSPECIFIED");
        var preview=await exporter.PreviewAsync(request,CancellationToken.None);
        Assert.True(preview.Success,preview.UserMessage);
        const long expected=10102;
        Assert.Equal(expected,Assert.IsType<JsonObject>(preview.Data)["personCount"]!.GetValue<long>());
        var exported=await exporter.ExportAsync(request,CancellationToken.None);
        Assert.True(exported.Success,exported.UserMessage);
        var info=Assert.IsType<JsonObject>(exported.Data);
        Assert.Equal(expected,info["personCount"]!.GetValue<long>());
        string file=info["filePath"]!.GetValue<string>();
        _exports.Add(file);
        Assert.False(File.Exists(file+".part"));
        using var zip=ZipFile.OpenRead(file);
        using var stream=zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        using var reader=System.Xml.XmlReader.Create(stream);
        int physicalRows=0;
        while(reader.Read())
            if(reader.NodeType==System.Xml.XmlNodeType.Element && reader.LocalName=="row")
                physicalRows++;
        Assert.Equal((int)expected+3,physicalRows);
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
