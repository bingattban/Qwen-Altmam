using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Genuine SQLCipher-to-OOXML round trip: never confuse two people's records
/// after reuse of الرقم (ع), and always audit only scope/filename metadata.
/// </summary>
public sealed class PersonnelExcelExportTests : IAsyncLifetime,IDisposable
{
    private readonly string _dbPath=SyntheticFixtures.CreateTempDatabasePath();
    private readonly string _folder=Path.Combine(Path.GetTempPath(),
        "altamam-personnel-export-"+Guid.NewGuid().ToString("N"));
    private AlTamamDbContext _db=null!;
    private static readonly BridgePrincipal Actor=new("TST-EXCEL-ACTOR","test-executor",
        UserRole.GeneralAdministration,"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _db=new AlTamamDbContext(_dbPath,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var conn=_db.CreateOpenConnection();
        using var cmd=conn.CreateCommand();
        cmd.CommandText="""
            INSERT INTO AppSettings(SettingKey,SettingValue,UpdatedAtUtc)
            VALUES('PreferredBackupDirectory',$folder,'2026-10-26T08:00:00Z')
            ON CONFLICT(SettingKey) DO UPDATE SET SettingValue=excluded.SettingValue;
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,
                FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,
                ActiveFlag,RowVersion)
            VALUES('TST-XLSX-OLD','الموظف السابق','=FORMULA','3333','3333',
                '2026-09-01','2026-09-01T00:00:00Z',3,0,2),
                  ('TST-XLSX-NEW','الموظف الجديد','الموظف الجديد','3333','3333',
                '2026-10-06','2026-10-06T00:00:00Z',1,1,1);
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('TST-XLSX-OLD','=HYPERLINK("http://evil.example")','3333',
                '2026-09-01',NULL,'TEST','2026-09-01T00:00:00Z','سجل سابق'),
                  ('TST-XLSX-NEW','الموظف الجديد','3333',
                '2026-10-06',NULL,'TEST','2026-10-06T00:00:00Z','سجل جديد مستقل');
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,ValidFromLocalDate,
                ValidToLocalDate,SourceMovementId,CreatedAtUtc)
            VALUES('TST-XLSX-A-OLD','TST-XLSX-OLD','SITE-UNSPECIFIED',
                '2026-09-01','2026-10-05',NULL,'2026-09-01T00:00:00Z'),
                  ('TST-XLSX-A-NEW','TST-XLSX-NEW','SITE-UNSPECIFIED',
                '2026-10-06',NULL,NULL,'2026-10-06T00:00:00Z');
            INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,StatusId,
                StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                Source,CreatedByUserId,CommittedAtUtc,IdempotencyKey)
            VALUES('TST-XLSX-OLD','2026-10-04','STS-ABSENT','غياب','غ','#ef4444',
                'TST-XLSX-A-OLD','SITE-UNSPECIFIED','غير محدد','manual','TEST',
                '2026-10-04T08:00:00Z','TST-XLSX-OLD-DAY'),
                  ('TST-XLSX-NEW','2026-10-07','STS-PRESENT','موجود','م','#38bdf8',
                'TST-XLSX-A-NEW','SITE-UNSPECIFIED','غير محدد','manual','TEST',
                '2026-10-07T08:00:00Z','TST-XLSX-NEW-DAY');
            INSERT INTO PersonnelMovements(MovementId,PersonId,PersonNameSnapshot,
                ServiceNumberASnapshot,MovementType,SourceSiteId,SourceSiteNameSnapshot,
                DestinationSiteId,DestinationSiteNameSnapshot,
                DestinationExternalName,SeparationReason,EffectiveLocalDate,
                CommittedAtUtc,ActorUserId,IdempotencyKey)
            VALUES('TST-XLSX-INT','TST-XLSX-OLD','الموظف السابق','3333',1,
                'SITE-UNSPECIFIED','غير محدد','SITE-UNSPECIFIED','غير محدد',
                NULL,NULL,'2026-10-01','2026-10-01T08:00:00Z','TEST','TST-MOV-INT'),
                  ('TST-XLSX-EXT','TST-XLSX-OLD','الموظف السابق','3333',2,
                'SITE-UNSPECIFIED','غير محدد',NULL,NULL,
                'جهة خارجية',NULL,'2026-10-03','2026-10-03T08:00:00Z','TEST','TST-MOV-EXT'),
                  ('TST-XLSX-SEP','TST-XLSX-OLD','الموظف السابق','3333',3,
                'SITE-UNSPECIFIED','غير محدد',NULL,NULL,
                NULL,'فصل معتمد','2026-10-05','2026-10-05T08:00:00Z','TEST','TST-MOV-SEP');
            """;
        cmd.Parameters.AddWithValue("$folder",_folder);
        await cmd.ExecuteNonQueryAsync();
        await _db.ExecuteInWriteTransactionAsync(async(c,t)=>{
            await BridgeAuditWriter.WriteAsync(c,t,TimeProvider.System,
                "PERSON_IDENTITY_CHANGE","تغيير هوية","TEST","test-executor",null,
                "Person","TST-XLSX-OLD","الموظف السابق",
                """{"name":"الاسم السابق"}""","""{"name":"الاسم الجديد"}""",
                "تغيير موثق","سبب التغيير","2026-10-04",
                Guid.NewGuid().ToString("N"),CancellationToken.None);
            return 0;
        });
    }
    private static string File(JsonObject o)=>o["filePath"]!.GetValue<string>();
    private static IEnumerable<XElement> Rows(ZipArchive zip,int index)
    {
        using var stream=zip.GetEntry($"xl/worksheets/sheet{index}.xml")!.Open();
        var doc=XDocument.Load(stream);
        XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return doc.Descendants(ns+"row").ToArray();
    }
    private static string[] Text(XElement row)
    {
        XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return row.Elements(ns+"c").Select(c=>
            c.Element(ns+"is")?.Element(ns+"t")?.Value??"").ToArray();
    }

    [Fact]
    public async Task GroupWorkbook_AllSheetsPreservePersonIdAndHistory_ExportAudited()
    {
        var svc=new PersonnelExcelExportService(_db);
        string id=Guid.NewGuid().ToString("N");
        var request=new JsonObject{
            ["mode"]="report",["exportId"]=id,
            ["personIds"]=new JsonArray("TST-XLSX-OLD","TST-XLSX-NEW"),
            ["dateFrom"]="2026-10-01",["dateTo"]="2026-10-26"
        };
        var preview=await svc.PreviewReportAsync(request,CancellationToken.None);
        Assert.True(preview.Success,preview.UserMessage);
        var p=Assert.IsType<JsonObject>(preview.Data);
        Assert.StartsWith("تقرير جماعي_",p["fileName"]!.GetValue<string>());
        Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd"),p["fileName"]!.GetValue<string>());
        Assert.Equal(_folder,p["directory"]!.GetValue<string>());
        var exported=await svc.ExportAsync((JsonObject)request.DeepClone(),Actor,CancellationToken.None);
        Assert.True(exported.Success,exported.UserMessage);
        var result=Assert.IsType<JsonObject>(exported.Data);
        Assert.Equal(p["fileName"]!.GetValue<string>(),result["fileName"]!.GetValue<string>());
        var file=File(result);
        Assert.True(System.IO.File.Exists(file));
        using(var zip=ZipFile.OpenRead(file))
        {
            Assert.Equal(7,zip.Entries.Count(e=>e.FullName.StartsWith("xl/worksheets/sheet")&&
                e.FullName.EndsWith(".xml")));
            var people=Rows(zip,1).Select(Text).ToArray();
            Assert.Equal(4,people.Length);
            Assert.Equal("TST-XLSX-OLD",people[1][0]);
            Assert.Equal("TST-XLSX-NEW",people[2][0]);
            Assert.Equal("3333",people[1][1]);
            Assert.Equal("3333",people[2][1]);
            Assert.Equal("إجمالي المجموعة",people[3][0]);
            Assert.Equal("2",people[3][5]);
            Assert.Contains("غياب (1)",people[3][9]);
            Assert.Contains("موجود (1)",people[3][9]);
            var history=Rows(zip,2).Select(Text).ToArray();
            Assert.Equal(3,history.Length);
            Assert.Equal("TST-XLSX-OLD",history[1][0]);
            Assert.Equal("TST-XLSX-NEW",history[2][0]);
            Assert.Equal("غياب",history[1][4]);
            Assert.Equal("موجود",history[2][4]);
            var movements=Rows(zip,3).Select(Text).ToArray();
            Assert.Equal(4,movements.Length);
            Assert.All(movements.Skip(1),x=>Assert.Equal("TST-XLSX-OLD",x[0]));
            var audits=Rows(zip,4).Select(Text).ToArray();
            Assert.Equal(2,audits.Length);
            Assert.Equal("""{"name":"الاسم السابق"}""",audits[1][7]);
            Assert.Equal("""{"name":"الاسم الجديد"}""",audits[1][8]);
            var names=Rows(zip,5).Select(Text).ToArray();
            Assert.Contains(names,x=>x.Contains("=HYPERLINK(\"http://evil.example\")"));
            foreach(var entry in zip.Entries.Where(e=>e.FullName.StartsWith("xl/worksheets/")))
            {
                using var stream=entry.Open();
                var xml=XDocument.Load(stream);
                XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                Assert.Empty(xml.Descendants(ns+"f")); // no spreadsheet-formula injection
            }
        }
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="SELECT NewValueJson FROM AuditEvents WHERE OperationCode='PERSONNEL_EXCEL_EXPORT';";
            var evidence=(string?)await q.ExecuteScalarAsync();
            Assert.NotNull(evidence);
            var meta=Assert.IsType<JsonObject>(JsonNode.Parse(evidence));
            Assert.StartsWith("تقرير جماعي_",meta["fileName"]!.GetValue<string>());
            Assert.Equal(2,meta["personCount"]!.GetValue<int>());
            Assert.DoesNotContain("الموظف السابق",meta.ToJsonString());
            Assert.DoesNotContain("الاسم الجديد",meta.ToJsonString());
        }
        Assert.False((await svc.ExportAsync(request,Actor,CancellationToken.None)).Success);
    }

    [Theory]
    [InlineData("internal","نقل داخلي","TST-XLSX-INT")]
    [InlineData("external","نقل خارجي","TST-XLSX-EXT")]
    public async Task MovementArchives_ExportOnlyMatchingType_WithNamedFiles(
        string type,string title,string movementId)
    {
        var svc=new PersonnelExcelExportService(_db);
        var payload=new JsonObject{["mode"]="movementArchive",["type"]=type,
            ["exportId"]=Guid.NewGuid().ToString("N")};
        var exported=await svc.ExportAsync(payload,Actor,CancellationToken.None);
        Assert.True(exported.Success,exported.UserMessage);
        var file=File(Assert.IsType<JsonObject>(exported.Data));
        Assert.StartsWith(title+"_",Path.GetFileName(file));
        Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd"),Path.GetFileName(file));
        using var zip=ZipFile.OpenRead(file);
        var rows=Rows(zip,1).Select(Text).ToArray();
        Assert.Equal(2,rows.Length);
        Assert.Equal(movementId,rows[1][0]);
        Assert.Equal("TST-XLSX-OLD",rows[1][1]);
    }

    [Fact]
    public async Task DismissalArchive_UsesOwnExcelFilename_AndRejectsInvalidPreview()
    {
        var svc=new PersonnelExcelExportService(_db);
        var r=await svc.ExportAsync(new JsonObject{
            ["mode"]="separationArchive",["exportId"]=Guid.NewGuid().ToString("N")
        },Actor,CancellationToken.None);
        Assert.True(r.Success,r.UserMessage);
        var file=File(Assert.IsType<JsonObject>(r.Data));
        Assert.StartsWith("أرشيف الفصل_",Path.GetFileName(file));
        using var zip=ZipFile.OpenRead(file);
        Assert.Equal("TST-XLSX-SEP",Text(Rows(zip,1).Last())[0]);
        Assert.False((await svc.PreviewReportAsync(new JsonObject{
            ["personId"]="TST-XLSX-NEW",["dateFrom"]="2026-12-01",["dateTo"]="2026-01-01"
        },CancellationToken.None)).Success);
        Assert.False((await svc.ExportAsync(new JsonObject{
            ["mode"]="movementArchive",["type"]="both"
        },Actor,CancellationToken.None)).Success);
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        try{Directory.Delete(_folder,true);}catch(IOException){}
        catch(UnauthorizedAccessException){}
        SyntheticFixtures.DeleteTempDatabase(_dbPath);
    }
}
