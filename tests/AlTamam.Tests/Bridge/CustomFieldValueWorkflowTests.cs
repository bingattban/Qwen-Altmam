using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

public sealed class CustomFieldValueWorkflowTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private static readonly BridgePrincipal Actor = new("TST-CUSTOM-EDITOR", "custom-editor",
        UserRole.GeneralAdministration, "abababababababababababababababab");
    private const string PersonName="فرد جديد للاعمدة";
    private const string Number="09770012";

    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }

    private static byte[] Workbook(string name,string number,string qualification)
    {
        using var buffer=new MemoryStream();
        using(var zip=new ZipArchive(buffer,ZipArchiveMode.Create,leaveOpen:true))
        {
            void Part(string path,string content)
            {
                using var stream=zip.CreateEntry(path,CompressionLevel.Optimal).Open();
                var bytes=Encoding.UTF8.GetBytes(content);
                stream.Write(bytes);
            }
            Part("[Content_Types].xml","<Types/>");
            Part("xl/workbook.xml","<workbook/>");
            static string Cell(string coordinate,string value) =>
                "<c r=\"" + coordinate + "\" t=\"inlineStr\"><is><t>" +
                System.Security.SecurityElement.Escape(value) + "</t></is></c>";
            string sheet="<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"
                + "<row r=\"1\">" + Cell("A1","الاسم") + Cell("B1","الرقم (ع)") +
                  Cell("C1","الموقع") + Cell("D1","المؤهل العلمي") + "</row>"
                + "<row r=\"2\">" + Cell("A2",name) + Cell("B2",number) +
                  Cell("C2","غير محدد") + Cell("D2",qualification) + "</row>"
                + "</sheetData></worksheet>";
            Part("xl/worksheets/sheet1.xml",sheet);
        }
        return buffer.ToArray();
    }

    private async Task<JsonObject> Column()
    {
        var answer=await new RosterColumnStructureService(_db).CreateAsync(
            new JsonObject
            {
                ["caption"]="المؤهل العلمي",["fieldType"]=1,
                ["operationId"]="TST-CUSTOM-COLUMN-"+Guid.NewGuid().ToString("N")
            },Actor,default);
        Assert.True(answer.Success,answer.UserMessage);
        return Assert.IsType<JsonObject>(answer.Data);
    }

    private async Task<string> CreatePerson()
    {
        var response=await new PersonRosterService(_db).CreatePersonAsync(
            Actor.UserId,Actor.Username,new JsonObject{
                ["visibleName"]=PersonName,["serviceNumberA"]=Number,
                ["siteId"]="SITE-UNSPECIFIED"
            },default);
        Assert.True(response.Success,response.UserMessage);
        return Assert.IsType<JsonObject>(response.Data)["personId"]!.GetValue<string>();
    }

    private string? Value(string personId,string fieldId)
    {
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="SELECT FieldValueText FROM CustomFieldValues WHERE PersonId=$p AND FieldDefinitionId=$f;";
        q.Parameters.AddWithValue("$p",personId);
        q.Parameters.AddWithValue("$f",fieldId);
        return q.ExecuteScalar() as string;
    }

    [Fact]
    public async Task EditColumns_ValidatesAndAudits_WithOptimisticConcurrency()
    {
        var column=await Column();
        string key=column["columnId"]!.GetValue<string>();
        string person=await CreatePerson();
        var service=new RosterCustomFieldValueService(_db);
        JsonObject Request(string val,long revision,string id)=>new()
        {
            ["personId"]=person,["expectedRevision"]=revision,
            ["values"]=new JsonObject { [key]=val },["operationId"]=id
        };
        var first=Request("بكالوريوس",1,"TST-CUSTOM-CHANGE-"+Guid.NewGuid().ToString("N"));
        var a=await service.UpdateAsync(first,Actor,default);
        Assert.True(a.Success,a.UserMessage);
        Assert.Equal("بكالوريوس",Value(person,key));
        var replay=await service.UpdateAsync(first,Actor,default);
        Assert.True(replay.Success,replay.UserMessage);
        var stale=await service.UpdateAsync(Request("دبلوم",1,"TST-CUSTOM-CHANGE-"+Guid.NewGuid().ToString("N")),Actor,default);
        Assert.False(stale.Success);
        var second=await service.UpdateAsync(Request("ماجستير",2,"TST-CUSTOM-CHANGE-"+Guid.NewGuid().ToString("N")),Actor,default);
        Assert.True(second.Success,second.UserMessage);
        Assert.Equal("ماجستير",Value(person,key));
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='CUSTOM_FIELD_VALUE_UPDATE' AND TargetEntityId=$p;";
        q.Parameters.AddWithValue("$p",person);
        Assert.Equal(2L,Convert.ToInt64(await q.ExecuteScalarAsync()));
        var rows=await new PersonRosterService(_db).QueryPersonsAsync(new JsonObject{["page"]=1,["pageSize"]=50},default);
        var items=Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(rows.Data)["items"]);
        var found=Assert.Single(items.OfType<JsonObject>(),p=>p["personId"]!.GetValue<string>()==person);
        Assert.Equal("ماجستير",found["customFields"]![key]!.GetValue<string>());
    }

    [Fact]
    public async Task ExcelExtraColumns_CreateNewPerson_ThenExplicitlyUpdateExisting()
    {
        var column=await Column();
        string field=column["columnId"]!.GetValue<string>();
        var service=new ExcelPersonnelImportService(_db);
        var first=await service.SelectAsync(new JsonObject{
            ["fileName"]="initial.xlsx",["base64"]=Convert.ToBase64String(Workbook(PersonName,Number,"بكالوريوس"))
        },Actor.UserId,default);
        Assert.True(first.Success,first.UserMessage);
        string firstToken=Assert.IsType<JsonObject>(first.Data)["workbookToken"]!.GetValue<string>();
        var firstPlan=await service.PreviewAsync(new JsonObject{["workbookToken"]=firstToken},Actor.UserId,default);
        Assert.True(firstPlan.Success,firstPlan.UserMessage);
        string preview=Assert.IsType<JsonObject>(firstPlan.Data)["previewToken"]!.GetValue<string>();
        var saved=await service.CommitAsync(new JsonObject{
            ["previewToken"]=preview,["operationId"]="TST-CUSTOM-EXCEL-"+Guid.NewGuid().ToString("N")
        },Actor.UserId,Actor.Username,default);
        Assert.True(saved.Success,saved.UserMessage);
        using var connection=_db.CreateOpenConnection();
        using var query=connection.CreateCommand();
        query.CommandText="SELECT PersonId FROM Persons WHERE CurrentServiceNumberA=$n;";
        query.Parameters.AddWithValue("$n",Number);
        string id=Assert.IsType<string>(await query.ExecuteScalarAsync());
        Assert.Equal("بكالوريوس",Value(id,field));

        var second=await service.SelectAsync(new JsonObject{
            ["fileName"]="updated.xlsx",["base64"]=Convert.ToBase64String(Workbook(PersonName,Number,"ماجستير")),
            ["updateExistingCustomFields"]=true
        },Actor.UserId,default);
        Assert.True(second.Success,second.UserMessage);
        string ticket=Assert.IsType<JsonObject>(second.Data)["workbookToken"]!.GetValue<string>();
        var plan=await service.PreviewAsync(new JsonObject{["workbookToken"]=ticket},Actor.UserId,default);
        Assert.True(plan.Success,plan.UserMessage);
        var details=Assert.IsType<JsonObject>(plan.Data);
        Assert.Equal(0,details["newCount"]!.GetValue<int>());
        Assert.Equal(1,details["customUpdateCount"]!.GetValue<int>());
        string planToken=details["previewToken"]!.GetValue<string>();
        var committed=await service.CommitAsync(new JsonObject{
            ["previewToken"]=planToken,
            ["approveCustomUpdates"]=true,
            ["operationId"]="TST-CUSTOM-EXCEL-"+Guid.NewGuid().ToString("N")
        },Actor.UserId,Actor.Username,default);
        Assert.True(committed.Success,committed.UserMessage);
        Assert.Equal(1,Assert.IsType<JsonObject>(committed.Data)["customUpdatedCount"]!.GetValue<int>());
        Assert.Equal("ماجستير",Value(id,field));
        query.Parameters.Clear();
        query.CommandText="SELECT COUNT(*) FROM Persons WHERE CurrentServiceNumberA=$n;";
        query.Parameters.AddWithValue("$n",Number);
        Assert.Equal(1L,Convert.ToInt64(await query.ExecuteScalarAsync()));
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
