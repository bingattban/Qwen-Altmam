using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

public sealed class StatusSymbolUniquenessTests : IAsyncLifetime, IDisposable
{
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db=null!;
    private static readonly BridgePrincipal Actor=new("TST-STATUS-ADMIN","administrator",
        UserRole.GeneralAdministration,"11111111111111111111111111111111");

    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }
    private static JsonObject Status(string name,string symbol,string color="#0099DD")
        =>new(){["name"]=name,["symbol"]=symbol,["color"]=color};
    private long Count(string sql)
    {
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();q.CommandText=sql;
        return Convert.ToInt64(q.ExecuteScalar());
    }
    private string Text(string sql)
    {
        using var c=_db.CreateOpenConnection();
        using var q=c.CreateCommand();q.CommandText=sql;
        return Convert.ToString(q.ExecuteScalar())??"";
    }

    [Fact]
    public async Task CreateAndUpdate_RejectEquivalentActiveSymbols_WhilePreservingRecords()
    {
        var service=new AdminConfigurationService(_db);
        var one=await service.CreateStatusAsync(Status("حالة اختبار الأولى","Xyz"),
            Actor,default);
        Assert.True(one.Success,one.UserMessage);
        string id=Assert.IsType<JsonObject>(one.Data)["statusId"]!.GetValue<string>();
        Assert.Equal(0L,Count("SELECT COUNT(*) FROM AttendanceRecords WHERE StatusId='"+id+"'"));
        var duplicate=await service.CreateStatusAsync(Status("حالة اختبار ثانية","xyz"),
            Actor,default);
        Assert.False(duplicate.Success);
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM AttendanceStatusDefinitions WHERE ShortSymbol='Xyz'"));
        var another=await service.CreateStatusAsync(Status("حالة اختبار ثالثة","ABC"),
            Actor,default);
        Assert.True(another.Success,another.UserMessage);
        string otherId=Assert.IsType<JsonObject>(another.Data)["statusId"]!.GetValue<string>();
        var edit=Status("حالة اختبار ثالثة معدلة","xYZ");
        edit["statusId"]=otherId;edit["expectedRevision"]=1L;
        var blocked=await service.UpdateStatusAsync(edit,Actor,default);
        Assert.False(blocked.Success);
        Assert.Equal("ABC",Text("SELECT ShortSymbol FROM AttendanceStatusDefinitions WHERE StatusId='"+otherId+"'"));
        Assert.Equal(2L,Count("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='STATUS_CREATE'"));
        Assert.Equal(0L,Count("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='STATUS_UPDATE'"));
    }

    [Fact]
    public async Task ArabicDiacriticsAreEquivalent_AndInactiveSymbolsAreReusable()
    {
        var svc=new AdminConfigurationService(_db);
        var first=await svc.CreateStatusAsync(Status("حالة رمز عربي أول","حَ"),Actor,default);
        Assert.True(first.Success,first.UserMessage);
        string id=Assert.IsType<JsonObject>(first.Data)["statusId"]!.GetValue<string>();
        var blocked=await svc.CreateStatusAsync(Status("حالة رمز عربي ثاني","ح"),Actor,default);
        Assert.False(blocked.Success);
        var disabled=await svc.DeactivateStatusAsync(new JsonObject{
            ["statusId"]=id,["expectedRevision"]=1L
        },Actor,default);
        Assert.True(disabled.Success,disabled.UserMessage);
        var reuse=await svc.CreateStatusAsync(Status("حالة رمز معاد","ح"),Actor,default);
        Assert.True(reuse.Success,reuse.UserMessage);
        Assert.Equal(2L,Count("SELECT COUNT(*) FROM AttendanceStatusDefinitions WHERE ShortSymbol IN ('ح','حَ')"));
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM AttendanceStatusDefinitions WHERE ActiveFlag=1 AND ShortSymbol='ح'"));
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
