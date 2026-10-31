using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Application.Roster;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

public sealed class Phase05RosterSecurityAcceptanceTests : IAsyncLifetime, IDisposable
{
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db=null!;
    private RosterAuthorizedCommands _gate=null!;
    private BridgePrincipal _founder=null!;

    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        var shared=new BridgeSharedServices(_db);
        var setup=await shared.ProvisionFirstAdministratorAsync("phase05-founder",
            "Abc123!phase05",default);
        Assert.True(setup.Success,setup.UserMessage);
        string id=Assert.IsType<JsonObject>(setup.Data)["userId"]!.GetValue<string>();
        _founder=new BridgePrincipal(id,"phase05-founder",
            UserRole.GeneralAdministration,"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        _gate=new RosterAuthorizedCommands(_db);
    }

    private long Count()
    {
        using var c=_db.CreateOpenConnection();using var q=c.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM Persons;";
        return Convert.ToInt64(q.ExecuteScalar());
    }

    [Fact]
    public async Task ProductGateway_RejectsForgedRoleAndUnknownAccount_WithoutWrites()
    {
        var input=new JsonObject{["visibleName"]="فرد أول للسلامة",
            ["serviceNumberA"]="90055",["siteId"]="SITE-UNSPECIFIED"};
        var rogue=new BridgePrincipal("USR-NOT-IN-DATABASE","intruder",
            UserRole.GeneralAdministration,"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var spoof=new BridgePrincipal(_founder.UserId,_founder.Username,
            UserRole.Supervisor,"cccccccccccccccccccccccccccccccc");
        Assert.False((await _gate.CreateAsync(rogue,input,default)).Success);
        Assert.False((await _gate.CreateAsync(spoof,input,default)).Success);
        Assert.False((await _gate.RemoveAsync(spoof,
            new JsonObject{["personIds"]=new JsonArray("nonexistent")},default)).Success);
        Assert.False((await _gate.PreviewRemovalAsync(spoof,
            new JsonObject{["personIds"]=new JsonArray("nonexistent")},default)).Success);
        Assert.Equal(0,Count());
    }

    [Fact]
    public async Task ProductGateway_CurrentDateAndNamedActiveDuplicate_AreEnforced()
    {
        var input=new JsonObject{["visibleName"]="أحمد سالم القديم",
            ["serviceNumberA"]="۰۰۲۲۱۱",["siteId"]="SITE-UNSPECIFIED"};
        var created=await _gate.CreateAsync(_founder,input,default);
        Assert.True(created.Success,created.UserMessage);
        Assert.Equal(1,Count());

        var duplicate=await _gate.CreateAsync(_founder,new JsonObject{
            ["visibleName"]="اسم مختلف لاحقاً",["serviceNumberA"]="٠٠٢٢١١",
            ["siteId"]="SITE-UNSPECIFIED"
        },default);
        Assert.False(duplicate.Success);
        Assert.Equal("DuplicateServiceNumber",duplicate.ErrorCode);
        Assert.Contains("أحمد سالم القديم",duplicate.UserMessage);
        Assert.Equal(1,Count());

        var historical=(JsonObject)input.DeepClone();
        historical["firstRecordedLocalDate"]=DateOnly.FromDateTime(DateTime.Today)
            .AddDays(-3).ToString("yyyy-MM-dd");
        var rejected=await _gate.CreateAsync(_founder,historical,default);
        Assert.False(rejected.Success);
        Assert.Equal("HistoricDayLocked",rejected.ErrorCode);
        Assert.Equal(1,Count());
    }

    [Fact]
    public async Task Catalog_NewUnusedStatusStartsAtZeroAndFourDefaultsExist()
    {
        var shared=new BridgeSharedServices(_db);
        var original=Assert.IsType<JsonArray>(
            (await shared.ListActiveStatusesAsync(default)).Data);
        foreach(string seededStatusId in new[]{"STS-PRESENT","STS-LEAVE","STS-ABSENT","STS-PERMISSION"})
            Assert.Contains(original,x=>x?["statusId"]?.GetValue<string>()==seededStatusId);
        var catalog=new AdminConfigurationService(_db);
        var added=await catalog.CreateStatusAsync(new JsonObject{
            ["name"]="حالة تجريبية مضافة",["symbol"]="ت",["color"]="#12ABCD",
            ["operationId"]="TEST-PHASE05-ADD-STATUS-0001"
        },_founder,default);
        Assert.True(added.Success,added.UserMessage);
        string id=Assert.IsType<JsonObject>(added.Data)["statusId"]!.GetValue<string>();
        var current=Assert.IsType<JsonArray>(
            (await shared.ListActiveStatusesAsync(default)).Data);
        var newStatus=Assert.Single(current.OfType<JsonObject>(),
            x=>x["statusId"]?.GetValue<string>()==id);
        Assert.Equal(0L,newStatus["todayCount"]!.GetValue<long>());
    }

    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose(){_db?.Dispose();SyntheticFixtures.DeleteTempDatabase(_path);}
}
