using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

public sealed class PersonnelFourDigitsAndSitesTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private const string Actor = "TST-SITES-ACTOR";
    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }
    private long Count(string sql)
    {
        using var c=_db.CreateOpenConnection();
        using var cmd=c.CreateCommand();
        cmd.CommandText=sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
    private string Read(string sql)
    {
        using var c=_db.CreateOpenConnection();
        using var cmd=c.CreateCommand();
        cmd.CommandText=sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    [Fact]
    public async Task SinglePerson_RequiresFourDigitsAndAnExistingSite()
    {
        var service = new PersonRosterService(_db);
        var invalid=await service.CreatePersonAsync(Actor,"tester",new JsonObject {
            ["visibleName"]="محمد صالح باصالح",["serviceNumberA"]="123",["siteName"]="قطاع الشمال"
        },CancellationToken.None);
        Assert.False(invalid.Success);
        Assert.Equal(0,Count("SELECT COUNT(*) FROM Persons"));
        Assert.Equal(0,Count("SELECT COUNT(*) FROM Sites WHERE NormalizedName<>'غير محدد'"));

        var unknownSite=await service.CreatePersonAsync(Actor,"tester",new JsonObject {
            ["visibleName"]="محمد صالح باصالح",["serviceNumberA"]="١٢٣٤",["siteName"]="قطاع الشمال"
        },CancellationToken.None);
        Assert.False(unknownSite.Success);
        Assert.Equal(0,Count("SELECT COUNT(*) FROM Sites WHERE VisibleName='قطاع الشمال'"));
        using(var c=_db.CreateOpenConnection())
        using(var seed=c.CreateCommand())
        {
            seed.CommandText="""
                INSERT INTO Sites(SiteId,VisibleName,NormalizedName,IsSystemSite,ActiveFlag,CreatedAtUtc,RowVersion)
                VALUES('SITE-EXISTING','قطاع الشمال','قطاع الشمال',0,1,'2026-01-01T00:00:00Z',1);
                INSERT INTO SiteNameHistory(SiteId,VisibleNameSnapshot,ValidFromLocalDate,ValidToLocalDate,ChangedByUserId,RecordedAtUtc)
                VALUES('SITE-EXISTING','قطاع الشمال','2026-01-01',NULL,'TST-SITES-ACTOR','2026-01-01T00:00:00Z');
                """;
            await seed.ExecuteNonQueryAsync();
        }
        var first=await service.CreatePersonAsync(Actor,"tester",new JsonObject {
            ["visibleName"]="محمد صالح باصالح",["serviceNumberA"]="١٢٣٤",["siteId"]="SITE-EXISTING"
        },CancellationToken.None);
        Assert.True(first.Success,first.UserMessage);
        Assert.Equal(1,Count("SELECT COUNT(*) FROM Sites WHERE VisibleName='قطاع الشمال'"));
        Assert.Equal("1234",Read("SELECT CurrentServiceNumberA FROM Persons WHERE NormalizedServiceNumberA='1234'"));
        Assert.Equal(1,Count("SELECT COUNT(*) FROM SiteNameHistory WHERE SiteId IN (SELECT SiteId FROM Sites WHERE VisibleName='قطاع الشمال')"));
        Assert.Equal(1,Count("SELECT COUNT(*) FROM PersonAssignments WHERE SiteId IN (SELECT SiteId FROM Sites WHERE VisibleName='قطاع الشمال')"));

        var second=await service.CreatePersonAsync(Actor,"tester",new JsonObject {
            ["visibleName"]="علي عبدالله الحضرمي",["serviceNumberA"]="12346",["siteId"]="SITE-EXISTING"
        },CancellationToken.None);
        Assert.True(second.Success,second.UserMessage);
        Assert.Equal(1,Count("SELECT COUNT(*) FROM Sites WHERE VisibleName='قطاع الشمال'"));
        Assert.Equal(2,Count("SELECT COUNT(*) FROM PersonAssignments WHERE SiteId IN (SELECT SiteId FROM Sites WHERE VisibleName='قطاع الشمال')"));
        Assert.Equal(2,Count("SELECT COUNT(*) FROM Persons"));
        Assert.Equal(2,Count("SELECT COUNT(*) FROM PersonIdentityHistory"));
        Assert.True(Count("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='PERSON_CREATE'")>=2);
    }

    [Fact]
    public async Task AddPerson_ArchivedNameOrNumberMatch_BlocksDuplicateIdentity()
    {
        var service = new PersonRosterService(_db);
        var first=await service.CreatePersonAsync(Actor,"tester",new JsonObject {
            ["visibleName"]="سالم باعيسى الحضرمي",["serviceNumberA"]="0088",
            ["firstRecordedLocalDate"]=DateOnly.FromDateTime(DateTime.Today).AddDays(-3).ToString("yyyy-MM-dd")
        },CancellationToken.None);
        Assert.True(first.Success,first.UserMessage);
        string personId=((JsonObject)first.Data!)["personId"]!.GetValue<string>();
        using(var c=_db.CreateOpenConnection())
        using(var exit=c.CreateCommand())
        {
            exit.CommandText="""
                UPDATE Persons SET OrganizationalStatus=2,ActiveFlag=0,RowVersion=RowVersion+1 WHERE PersonId=$id;
                UPDATE PersonAssignments SET ValidToLocalDate=$day WHERE PersonId=$id;
                """;
            exit.Parameters.AddWithValue("$id",personId);
            exit.Parameters.AddWithValue("$day",DateOnly.FromDateTime(DateTime.Today).AddDays(-1).ToString("yyyy-MM-dd"));
            await exit.ExecuteNonQueryAsync();
        }
        foreach(var (name,number) in new[] {
            ("اسم آخر لا يمثل الشخص","0088"),
            ("سالم باعيسى الحضرمي","9988")
        })
        {
            var proposed=await service.CreatePersonAsync(Actor,"tester",new JsonObject {
                ["visibleName"]=name,["serviceNumberA"]=number
            },CancellationToken.None);
            Assert.False(proposed.Success);
            Assert.Contains("الأرشيف",proposed.UserMessage);
        }
        Assert.Equal(1,Count("SELECT COUNT(*) FROM Persons"));
    }

    private static JsonObject Workbook()
    {
        using var stream = new MemoryStream();
        using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,leaveOpen:true))
        {
            void Add(string name,string xml)
            {
                using var s=zip.CreateEntry(name).Open();
                var bytes=Encoding.UTF8.GetBytes(xml);s.Write(bytes);
            }
            Add("[Content_Types].xml","<Types/>");
            Add("xl/workbook.xml","<workbook/>");
            Add("xl/worksheets/sheet1.xml","""
                <?xml version="1.0" encoding="utf-8"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                  <row r="1">
                    <c r="A1" t="inlineStr"><is><t>الرقم</t></is></c>
                    <c r="B1" t="inlineStr"><is><t>الاسم</t></is></c>
                    <c r="C1" t="inlineStr"><is><t>الرقم (ع)</t></is></c>
                    <c r="D1" t="inlineStr"><is><t>الموقع</t></is></c>
                  </row>
                  <row r="2">
                    <c r="A2"><v>1</v></c><c r="B2" t="inlineStr"><is><t>محمد كرامة عبد الله</t></is></c>
                    <c r="C2"><v>12345</v></c><c r="D2" t="inlineStr"><is><t>الموقع الشرقي</t></is></c>
                  </row>
                  <row r="3">
                    <c r="A3"><v>2</v></c><c r="B3" t="inlineStr"><is><t>سعيد أحمد عبدالله</t></is></c>
                    <c r="C3"><v>12346</v></c><c r="D3" t="inlineStr"><is><t>الموقع الشرقي</t></is></c>
                  </row>
                  <row r="4">
                    <c r="A4"><v>3</v></c><c r="B4" t="inlineStr"><is><t>خالد محمد سالم</t></is></c>
                    <c r="C4"><v>123</v></c><c r="D4" t="inlineStr"><is><t>الموقع الغربي</t></is></c>
                  </row>
                </sheetData></worksheet>
                """);
        }
        return new JsonObject {["fileName"]="القوة.xlsx",
            ["base64"]=Convert.ToBase64String(stream.ToArray())};
    }

    [Fact]
    public async Task Excel_UsesOnlyServiceNumberColumn_CreatesSitesForValidRows()
    {
        var service=new ExcelPersonnelImportService(_db);
        var selected=await service.SelectAsync(Workbook(),Actor,CancellationToken.None);
        Assert.True(selected.Success,selected.UserMessage);
        string workbook=((JsonObject)selected.Data!)["workbookToken"]!.GetValue<string>();
        var analysis=await service.AnalyzeAsync(new JsonObject {["workbookToken"]=workbook},
            Actor,CancellationToken.None);
        Assert.True(analysis.Success,analysis.UserMessage);
        var report=(JsonObject)analysis.Data!;
        Assert.Equal(2,report["newCount"]!.GetValue<int>());
        Assert.Equal(1,report["issueCount"]!.GetValue<int>());
        var preview=await service.PreviewAsync(new JsonObject {["workbookToken"]=workbook},Actor,CancellationToken.None);
        Assert.True(preview.Success,preview.UserMessage);
        string token=((JsonObject)preview.Data!)["previewToken"]!.GetValue<string>();
        var committed=await service.CommitAsync(new JsonObject {["previewToken"]=token},
            Actor,"tester",CancellationToken.None);
        Assert.True(committed.Success,committed.UserMessage);
        Assert.Equal(2,Count("SELECT COUNT(*) FROM Persons"));
        Assert.Equal(1,Count("SELECT COUNT(*) FROM Sites WHERE VisibleName='الموقع الشرقي'"));
        Assert.Equal(0,Count("SELECT COUNT(*) FROM Sites WHERE VisibleName='الموقع الغربي'"));
        Assert.Equal(2,Count("SELECT COUNT(*) FROM PersonAssignments WHERE SiteId IN (SELECT SiteId FROM Sites WHERE VisibleName='الموقع الشرقي')"));
        Assert.Equal(1,((JsonObject)committed.Data!)["createdSitesCount"]!.GetValue<int>());
        Assert.Equal("الموقع الغربي",Read("SELECT RawSiteName FROM ImportIssues LIMIT 1"));
        Assert.Equal(1,Count("SELECT COUNT(*) FROM ImportIssues"));
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
