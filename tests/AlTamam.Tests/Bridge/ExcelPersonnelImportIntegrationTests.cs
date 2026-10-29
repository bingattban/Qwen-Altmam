using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

public sealed class ExcelPersonnelImportIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private const string Owner = "TST-IMPORT-ACTOR";
    private ExcelPersonnelImportService Service => new(_db);

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }

    private static byte[] Workbook(params (string name, string number)[] entries)
        => WorkbookAtSite("غير محدد", entries);

    private static byte[] WorkbookAtSite(string siteName,params (string name, string number)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Part(string name, string content)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                byte[] data = Encoding.UTF8.GetBytes(content);
                s.Write(data);
            }
            Part("[Content_Types].xml", "<Types/>");
            Part("xl/workbook.xml", "<workbook/>");
            var xml = new StringBuilder("""
                <?xml version="1.0" encoding="utf-8"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>الاسم</t></is></c>
                <c r="B1" t="inlineStr"><is><t>الرقم (ع)</t></is></c>
                <c r="C1" t="inlineStr"><is><t>الموقع</t></is></c></row>
                """);
            for(int i=0;i<entries.Length;i++)
            {
                string name=System.Security.SecurityElement.Escape(entries[i].name)!;
                string num=System.Security.SecurityElement.Escape(entries[i].number)!;
                int row=i+2;
                xml.Append($"""<row r="{row}"><c r="A{row}" t="inlineStr"><is><t>{name}</t></is></c>""");
                string safeSite=System.Security.SecurityElement.Escape(siteName)!;
                xml.Append($"""<c r="B{row}" t="inlineStr"><is><t>{num}</t></is></c>""");
                xml.Append($"""<c r="C{row}" t="inlineStr"><is><t>{safeSite}</t></is></c></row>""");
            }
            xml.Append("</sheetData></worksheet>");
            Part("xl/worksheets/sheet1.xml",xml.ToString());
        }
        return buffer.ToArray();
    }

    private static JsonObject FilePayload(byte[] workbook) => new()
    {
        ["fileName"]="كشف_القوة.xlsx", ["base64"]=Convert.ToBase64String(workbook)
    };

    private string Scalar(string sql)
    {
        using var connection=_db.CreateOpenConnection();
        using var cmd=connection.CreateCommand();
        cmd.CommandText=sql;
        return Convert.ToString(cmd.ExecuteScalar())??"";
    }

    [Fact]
    public async Task Import_AddsNewPeopleAndIdentityHistoryOnly_Audited_WithoutAttendance()
    {
        var select=await Service.SelectAsync(FilePayload(Workbook(
            ("عبدالله سالم بن عمر","000123"),("محمد أحمد باصالح","000124"))), Owner, CancellationToken.None);
        Assert.True(select.Success,select.UserMessage);
        var ticket=((JsonObject)select.Data!)["workbookToken"]!.GetValue<string>();
        var analysis=await Service.AnalyzeAsync(new JsonObject{["workbookToken"]=ticket},Owner,CancellationToken.None);
        Assert.True(analysis.Success,analysis.UserMessage);
        Assert.Equal(2,((JsonObject)analysis.Data!)["newCount"]!.GetValue<int>());
        var preview=await Service.PreviewAsync(new JsonObject{["workbookToken"]=ticket},Owner,CancellationToken.None);
        Assert.True(preview.Success,preview.UserMessage);
        var details=(JsonObject)preview.Data!;
        Assert.True(details["requiresExplicitConfirmation"]!.GetValue<bool>());
        Assert.Equal("0",Scalar("SELECT COUNT(*) FROM Persons"));
        var token=details["previewToken"]!.GetValue<string>();
        var saved=await Service.CommitAsync(new JsonObject{["previewToken"]=token},Owner,"tst-actor",CancellationToken.None);
        Assert.True(saved.Success,saved.UserMessage);
        Assert.Equal(2,((JsonObject)saved.Data!)["createdCount"]!.GetValue<int>());
        Assert.Equal("2",Scalar("SELECT COUNT(*) FROM Persons"));
        Assert.Equal("2",Scalar("SELECT COUNT(*) FROM PersonIdentityHistory"));
        Assert.Equal("2",Scalar("SELECT COUNT(*) FROM PersonAssignments WHERE SiteId='SITE-UNSPECIFIED'"));
        Assert.Equal("0",Scalar("SELECT COUNT(*) FROM AttendanceRecords"));
        Assert.Equal("000123",Scalar("SELECT CurrentServiceNumberA FROM Persons WHERE CurrentServiceNumberA='000123'"));
        Assert.Equal("PERSONNEL_IMPORT",Scalar("SELECT OperationCode FROM AuditEvents ORDER BY rowid DESC LIMIT 1"));
        var replay=await Service.CommitAsync(new JsonObject{["previewToken"]=token},Owner,"tst-actor",CancellationToken.None);
        Assert.False(replay.Success);
        Assert.Equal("2",Scalar("SELECT COUNT(*) FROM Persons"));
    }


    [Fact]
    public async Task CommittedExcelImport_LostResponseRetry_ReturnsSameBatchAndOneAudit()
    {
        var file = await Service.SelectAsync(FilePayload(Workbook(
            ("فرد الاستيراد المستقل", "00991"))), Owner, CancellationToken.None);
        Assert.True(file.Success,file.UserMessage);
        string ticket=((JsonObject)file.Data!)["workbookToken"]!.GetValue<string>();
        var preview=await Service.PreviewAsync(new JsonObject{
            ["workbookToken"]=ticket},Owner,CancellationToken.None);
        Assert.True(preview.Success,preview.UserMessage);
        var commit=new JsonObject{
            ["previewToken"]=((JsonObject)preview.Data!)["previewToken"]!.GetValue<string>(),
            ["operationId"]="section02-import-batch-00991"
        };
        var first=await Service.CommitAsync(commit,Owner,"owner",CancellationToken.None);
        Assert.True(first.Success,first.UserMessage);
        var retry=await Service.CommitAsync((JsonObject)commit.DeepClone(),
            Owner,"owner",CancellationToken.None);
        Assert.True(retry.Success,retry.UserMessage);
        Assert.Equal(((JsonObject)first.Data!).ToJsonString(),
            ((JsonObject)retry.Data!).ToJsonString());
        Assert.Equal("1",Scalar("SELECT COUNT(*) FROM Persons"));
        Assert.Equal("1",Scalar("SELECT COUNT(*) FROM ImportBatches WHERE Status='COMMITTED'"));
        Assert.Equal("1",Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='PERSONNEL_IMPORT'"));
        Assert.Equal("1",Scalar("SELECT COUNT(*) FROM MutationReceipts"));
        var other=(JsonObject)commit.DeepClone();
        other["previewToken"]=new string('f',64);
        Assert.False((await Service.CommitAsync(other,Owner,"owner",CancellationToken.None)).Success);
    }

    [Fact]
    public async Task DuplicateWithinFileAndPreviouslyImportedNumber_AreReportedButNeverOverwritten()
    {
        var first=await Service.SelectAsync(FilePayload(Workbook(("خالد سعيد الحضرمي","00001"))),Owner,CancellationToken.None);
        string file1=((JsonObject)first.Data!)["workbookToken"]!.GetValue<string>();
        var p1=await Service.PreviewAsync(new JsonObject{["workbookToken"]=file1},Owner,CancellationToken.None);
        string token1=((JsonObject)p1.Data!)["previewToken"]!.GetValue<string>();
        Assert.True((await Service.CommitAsync(new JsonObject{["previewToken"]=token1},Owner,"owner",CancellationToken.None)).Success);

        var second=await Service.SelectAsync(FilePayload(Workbook(
            ("اسم بديل لا يجوز","00001"),("سعيد عبدالله منصر","00002"),("سعيد عبدالله منصر","00002"))),
            Owner,CancellationToken.None);
        string file2=((JsonObject)second.Data!)["workbookToken"]!.GetValue<string>();
        var analyzed=await Service.AnalyzeAsync(new JsonObject{["workbookToken"]=file2},Owner,CancellationToken.None);
        var summary=(JsonObject)analyzed.Data!;
        Assert.Equal(1,summary["newCount"]!.GetValue<int>());
        Assert.Equal(2,summary["issueCount"]!.GetValue<int>());
        var preview=await Service.PreviewAsync(new JsonObject{["workbookToken"]=file2},Owner,CancellationToken.None);
        string token2=((JsonObject)preview.Data!)["previewToken"]!.GetValue<string>();
        var saved=await Service.CommitAsync(new JsonObject{["previewToken"]=token2},Owner,"owner",CancellationToken.None);
        Assert.True(saved.Success,saved.UserMessage);
        Assert.Equal("2",Scalar("SELECT COUNT(*) FROM Persons"));
        Assert.Equal("خالد سعيد الحضرمي",Scalar("SELECT CurrentVisibleName FROM Persons WHERE CurrentServiceNumberA='00001'"));
    }

    [Fact]
    public async Task StalePlanAndWrongUserAreDeniedBeforeWriting()
    {
        var selected=await Service.SelectAsync(FilePayload(Workbook(("فارس محمد صالح","00010"))),Owner,CancellationToken.None);
        var ticket=((JsonObject)selected.Data!)["workbookToken"]!.GetValue<string>();
        var other=await Service.AnalyzeAsync(new JsonObject{["workbookToken"]=ticket},"ANOTHER-USER",CancellationToken.None);
        Assert.False(other.Success);
        var preview=await Service.PreviewAsync(new JsonObject{["workbookToken"]=ticket},Owner,CancellationToken.None);
        var token=((JsonObject)preview.Data!)["previewToken"]!.GetValue<string>();
        var foreign=await Service.CommitAsync(new JsonObject{["previewToken"]=token},"ANOTHER-USER","foreign",CancellationToken.None);
        Assert.False(foreign.Success);
        Assert.Equal("0",Scalar("SELECT COUNT(*) FROM Persons"));
        var byOwner=await Service.CommitAsync(new JsonObject{["previewToken"]=token},Owner,"owner",CancellationToken.None);
        Assert.True(byOwner.Success,byOwner.UserMessage);
    }

    [Fact]
    public async Task NewSiteRequiresSeparateExplicitApproval_AndIsAuditedOnce()
    {
        var file=await Service.SelectAsync(FilePayload(WorkbookAtSite("موقع المطار",
            ("فرد تجريبي أول","00771"),("فرد تجريبي ثان","00772"))),Owner,CancellationToken.None);
        Assert.True(file.Success,file.UserMessage);
        string workbookToken=((JsonObject)file.Data!)["workbookToken"]!.GetValue<string>();
        var preview=await Service.PreviewAsync(new JsonObject{
            ["workbookToken"]=workbookToken},Owner,CancellationToken.None);
        Assert.True(preview.Success,preview.UserMessage);
        var data=Assert.IsType<JsonObject>(preview.Data);
        var sites=Assert.IsType<JsonArray>(data["newSites"]);
        var first=Assert.IsType<JsonObject>(Assert.Single(sites));
        Assert.Equal("موقع المطار",first["name"]!.GetValue<string>());
        Assert.Equal(2,first["personCount"]!.GetValue<int>());
        string ticket=data["previewToken"]!.GetValue<string>();
        var denied=await Service.CommitAsync(new JsonObject{
            ["previewToken"]=ticket},Owner,"owner",CancellationToken.None);
        Assert.False(denied.Success);
        Assert.Equal("0",Scalar("SELECT COUNT(*) FROM Persons"));
        Assert.Equal("0",Scalar("SELECT COUNT(*) FROM Sites WHERE VisibleName='موقع المطار'"));
        var wrong=await Service.CommitAsync(new JsonObject{
            ["previewToken"]=ticket,["confirmNewSites"]=true,
            ["approvedSiteNames"]=new JsonArray("موقع آخر")
        },Owner,"owner",CancellationToken.None);
        Assert.False(wrong.Success);
        var approved=await Service.CommitAsync(new JsonObject{
            ["previewToken"]=ticket,["confirmNewSites"]=true,
            ["approvedSiteNames"]=new JsonArray("موقع المطار")
        },Owner,"owner",CancellationToken.None);
        Assert.True(approved.Success,approved.UserMessage);
        Assert.Equal("2",Scalar("SELECT COUNT(*) FROM Persons"));
        Assert.Equal("1",Scalar("SELECT COUNT(*) FROM Sites WHERE VisibleName='موقع المطار'"));
        Assert.Equal("1",Scalar("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='SITE_CREATE'"));
    }

    [Fact]
    public async Task InvalidWorkbookAndNumericServiceNumberAreRejected()
    {
        var fake=await Service.SelectAsync(new JsonObject {
            ["fileName"]="wrong.xlsx",["base64"]=Convert.ToBase64String(Encoding.UTF8.GetBytes("not excel"))
        },Owner,CancellationToken.None);
        Assert.False(fake.Success);
        var bytes=Workbook(("محمد خالد سالم","00005"));
        using(var input=new MemoryStream(bytes))
        using(var zip=new ZipArchive(input,ZipArchiveMode.Read))
            Assert.NotNull(zip.GetEntry("xl/worksheets/sheet1.xml"));
        var denied=await Service.SelectAsync(FilePayload(new byte[1_048_577]),Owner,CancellationToken.None);
        Assert.False(denied.Success);
        Assert.Equal("0",Scalar("SELECT COUNT(*) FROM Persons"));
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
