using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

public sealed class RosterColumnStructureIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db=null!;
    private static readonly BridgePrincipal Actor=new(
        "TST-LAYOUT-ACTOR","layout-owner",UserRole.GeneralAdministration,
        "0123456789abcdef0123456789abcdef");
    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();
        q.CommandText="""
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,
                FirstRecordedLocalDate,FirstRecordedAtUtc,
                OrganizationalStatus,ActiveFlag,RowVersion)
            VALUES('TST-LAYOUT-PERSON','صاحب العمود','صاحب العمود','3344','3344',
                '2026-10-01','2026-10-01T08:00:00Z',1,1,1);
            """;
        await q.ExecuteNonQueryAsync();
    }
    private static JsonObject Create(string caption,int type,string operationId)
        => new(){
            ["caption"]=caption,["fieldType"]=type,["operationId"]=operationId
        };
    private static JsonObject Update(JsonObject col,string? caption=null,int? type=null,
        int? position=null,bool? visible=null,string? operationId=null)
        => new(){
            ["columnId"]=col["columnId"]!.GetValue<string>(),
            ["expectedSnapshot"]=col["snapshot"]!.GetValue<string>(),
            ["caption"]=caption??col["caption"]!.GetValue<string>(),
            ["fieldType"]=type??col["fieldType"]!.GetValue<int>(),
            ["displayOrder"]=position??col["displayOrder"]!.GetValue<int>(),
            ["visible"]=visible??col["visible"]!.GetValue<bool>(),
            ["operationId"]=operationId??Guid.NewGuid().ToString("N")
        };
    private static async Task<JsonObject[]> Items(RosterColumnStructureService svc,
        bool inactive=false)
    {
        var r=await svc.ListAsync(new JsonObject{["includeInactive"]=inactive},default);
        Assert.True(r.Success,r.UserMessage);
        return ((JsonArray)((JsonObject)r.Data!)["items"]!)
            .Select(x=>(JsonObject)x!.DeepClone()).ToArray();
    }
    private static string Id(JsonObject c)=>c["columnId"]!.GetValue<string>();
    private long Count(string query)
    {
        using var c=_db.CreateOpenConnection();using var q=c.CreateCommand();
        q.CommandText=query;return Convert.ToInt64(q.ExecuteScalar());
    }

    [Fact]
    public async Task AddRenameReorderHide_DoesNotChangeStableColumnIdOrOtherValues()
    {
        var svc=new RosterColumnStructureService(_db);
        var initial=await Items(svc);
        Assert.Equal(4,initial.Length);
        Assert.All(initial,x=>Assert.True(x["isSystemProtected"]!.GetValue<bool>()));
        var first=await svc.CreateAsync(Create("رقم آخر",1,"layout-create-first-20261026"),
            Actor,default);
        Assert.True(first.Success,first.UserMessage);
        var same=await svc.CreateAsync(Create("رقم آخر",1,"layout-create-first-20261026"),
            Actor,default);
        Assert.True(same.Success,same.UserMessage);
        Assert.Equal(Id(Assert.IsType<JsonObject>(first.Data)),Id(Assert.IsType<JsonObject>(same.Data)));
        var normalized=await svc.CreateAsync(Create("رَقَم آخر",1,"layout-create-duplicate-001"),Actor,default);
        Assert.False(normalized.Success);
        var second=await svc.CreateAsync(Create("تاريخ الالتحاق",3,"layout-create-second-20261026"),
            Actor,default);
        Assert.True(second.Success,second.UserMessage);
        var a=Assert.IsType<JsonObject>(first.Data);
        var b=Assert.IsType<JsonObject>(second.Data);
        Assert.StartsWith("FLD-",Id(a));
        Assert.Equal(5,a["displayOrder"]!.GetValue<int>());
        Assert.Equal(6,b["displayOrder"]!.GetValue<int>());
        string key=b["technicalKey"]!.GetValue<string>();
        var move=await svc.UpdateAsync(Update(b,position:5),Actor,default);
        Assert.True(move.Success,move.UserMessage);
        var rows=await Items(svc);
        Assert.Equal(6,rows.Length);
        var original=rows.Single(x=>Id(x)==Id(a));
        var moved=rows.Single(x=>Id(x)==Id(b));
        Assert.Equal(6,original["displayOrder"]!.GetValue<int>());
        Assert.Equal(5,moved["displayOrder"]!.GetValue<int>());
        Assert.Equal(key,moved["technicalKey"]!.GetValue<string>());
        var rename=await svc.UpdateAsync(Update(moved,caption:"تاريخ الالتحاق الجديد",
            visible:false),Actor,default);
        Assert.True(rename.Success,rename.UserMessage);
        var saved=Assert.IsType<JsonObject>(rename.Data);
        Assert.Equal(Id(b),Id(saved));
        Assert.Equal(key,saved["technicalKey"]!.GetValue<string>());
        Assert.False(saved["visible"]!.GetValue<bool>());
        Assert.Equal(2L,Count("SELECT COUNT(*) FROM CustomFieldDefinitions WHERE IsSystemProtected=0 AND ActiveFlag=1"));
        Assert.Equal(2L,Count("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='ROSTER_COLUMN_CREATE'"));
        Assert.Equal(2L,Count("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='ROSTER_COLUMN_EDIT'"));
        var stale=await svc.UpdateAsync(Update(b,caption:"تعديل قديم"),Actor,default);
        Assert.False(stale.Success);
        Assert.Equal(BridgeErrorCodes.ConflictStale,stale.ErrorCode);
    }

    [Fact]
    public async Task StoredValuePreservedWhenHiddenOrDeactivated_TypeChangesWithValuesRejected()
    {
        var svc=new RosterColumnStructureService(_db);
        var created=await svc.CreateAsync(Create("رقم إضافي",2,"layout-value-create-20261026"),
            Actor,default);
        Assert.True(created.Success,created.UserMessage);
        var column=Assert.IsType<JsonObject>(created.Data);
        string id=Id(column);
        using(var c=_db.CreateOpenConnection())
        using(var q=c.CreateCommand())
        {
            q.CommandText="""
                INSERT INTO CustomFieldValues(PersonId,FieldDefinitionId,FieldValueText,UpdatedAtUtc)
                VALUES('TST-LAYOUT-PERSON',$field,'007','2026-10-26T08:00:00Z');
                """;
            q.Parameters.AddWithValue("$field",id);
            await q.ExecuteNonQueryAsync();
        }
        var list=await Items(svc);
        var current=list.Single(x=>Id(x)==id);
        Assert.Equal(1L,current["storedValueCount"]!.GetValue<long>());
        var typeChange=await svc.UpdateAsync(Update(current,type:3),Actor,default);
        Assert.False(typeChange.Success);
        var hidden=await svc.UpdateAsync(Update(current,visible:false),Actor,default);
        Assert.True(hidden.Success,hidden.UserMessage);
        var after=Assert.IsType<JsonObject>(hidden.Data);
        Assert.Equal(2,after["fieldType"]!.GetValue<int>());
        var denied=await svc.DeactivateAsync(new JsonObject{
            ["columnId"]=id,["expectedSnapshot"]=column["snapshot"]!.GetValue<string>(),
            ["confirmed"]=true,["operationId"]="layout-retire-stale-20261026"
        },Actor,default);
        Assert.False(denied.Success);
        Assert.Equal(BridgeErrorCodes.ConflictStale,denied.ErrorCode);
        var request=new JsonObject{
            ["columnId"]=id,["expectedSnapshot"]=after["snapshot"]!.GetValue<string>(),
            ["confirmed"]=true,["operationId"]="layout-retire-once-20261026"
        };
        var retire=await svc.DeactivateAsync(request,Actor,default);
        Assert.True(retire.Success,retire.UserMessage);
        var replay=await svc.DeactivateAsync((JsonObject)request.DeepClone(),Actor,default);
        Assert.True(replay.Success,replay.UserMessage);
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM CustomFieldValues WHERE FieldValueText='007'"));
        Assert.Equal(0L,Count("SELECT COUNT(*) FROM CustomFieldDefinitions WHERE FieldDefinitionId='"+id+"' AND ActiveFlag=1"));
        Assert.Single(await Items(svc,true),x=>Id(x)==id);
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='ROSTER_COLUMN_DEACTIVATE'"));
        using var conn=_db.CreateOpenConnection();using var integrity=conn.CreateCommand();
        integrity.CommandText="PRAGMA foreign_key_check;";
        using var reader=await integrity.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task ProtectedSystemColumnsCannotBeChangedOrDisabledAndTypesAreValidated()
    {
        var svc=new RosterColumnStructureService(_db);
        var system=(await Items(svc)).Single(x=>x["technicalKey"]!.GetValue<string>()=="sys_person_name");
        var result=await svc.UpdateAsync(Update(system,caption:"غير الاسم",position:5),Actor,default);
        Assert.False(result.Success);
        var removal=await svc.DeactivateAsync(new JsonObject{
            ["columnId"]=Id(system),
            ["expectedSnapshot"]=system["snapshot"]!.GetValue<string>(),
            ["confirmed"]=true,["operationId"]="layout-system-delete-20261026"
        },Actor,default);
        Assert.False(removal.Success);
        Assert.False((await svc.CreateAsync(Create("عمود سيئ",8,"layout-invalid-type-20261026"),
            Actor,default)).Success);
        var draft=await svc.CreateAsync(Create("تاريخ جديد",1,"layout-newtype-create-20261026"),
            Actor,default);
        Assert.True(draft.Success,draft.UserMessage);
        var column=Assert.IsType<JsonObject>(draft.Data);
        var typed=await svc.UpdateAsync(Update(column,type:3),Actor,default);
        Assert.True(typed.Success,typed.UserMessage);
        Assert.Equal(3,Assert.IsType<JsonObject>(typed.Data)["fieldType"]!.GetValue<int>());
        Assert.Equal(0L,Count("SELECT COUNT(*) FROM CustomFieldValues"));
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose(){_db?.Dispose();SyntheticFixtures.DeleteTempDatabase(_path);}
}
