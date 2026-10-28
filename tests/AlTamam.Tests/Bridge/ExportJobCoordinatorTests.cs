using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Deterministic concurrency and security checks for real export progress/cancellation.</summary>
public sealed class ExportJobCoordinatorTests
{
    private static string Session(int n) => new string((char)('a' + n), 32);

    [Fact]
    public void NoJob_MustReturnMissingDataNeverInventACompletion()
    {
        var manager = new ExportJobCoordinator();
        Assert.Equal(BridgeErrorCodes.MissingData, manager.Get(Session(0)).ErrorCode);
        Assert.Equal(BridgeErrorCodes.MissingData, manager.Cancel(Session(0)).ErrorCode);
    }

    [Fact]
    public void RunningExport_ReportsBoundedFactsAndOnlyItsSessionMayCancel()
    {
        var manager = new ExportJobCoordinator();
        Assert.True(manager.TryBegin(Session(0), "EXPORT-A", "monthly", CancellationToken.None, out var job));
        Assert.NotNull(job);
        using(job!)
        {
            job!.Report(42, 100);
            var current = Assert.IsType<JsonObject>(manager.Get(Session(0)).Data);
            Assert.Equal("running",current["status"]!.GetValue<string>());
            Assert.Equal(42,current["processed"]!.GetValue<int>());
            Assert.Equal(100,current["total"]!.GetValue<int>());
            Assert.Equal(42,current["percentage"]!.GetValue<int>());
            Assert.Equal("EXPORT-A",current["jobId"]!.GetValue<string>());
            Assert.Equal(BridgeErrorCodes.MissingData,manager.Get(Session(1)).ErrorCode);
            Assert.Equal(BridgeErrorCodes.MissingData,manager.Cancel(Session(1)).ErrorCode);
            Assert.False(job!.Token.IsCancellationRequested);
            var canceled=manager.Cancel(Session(0));
            Assert.True(canceled.Success,canceled.UserMessage);
            Assert.True(job!.Token.IsCancellationRequested);
            var after=Assert.IsType<JsonObject>(manager.Get(Session(0)).Data);
            Assert.True(after["cancelRequested"]!.GetValue<bool>());
            Assert.Equal("canceling",after["status"]!.GetValue<string>());
            Assert.False(after["completed"]!.GetValue<bool>());
            job!.Finish(false);
        }
        var final=Assert.IsType<JsonObject>(manager.Get(Session(0)).Data);
        Assert.Equal("canceled",final["status"]!.GetValue<string>());
        Assert.False(final["completed"]!.GetValue<bool>());
        Assert.Null(final["filePath"]);
    }

    [Fact]
    public void CompletedFileOnlyAfterWriterActuallyReportsSuccess()
    {
        var manager=new ExportJobCoordinator();
        Assert.True(manager.TryBegin(Session(0),"EXPORT-B","personnel",CancellationToken.None,out var job));
        using(job!)
        {
            job!.Report(100,100);
            Assert.Equal(99,((JsonObject)manager.Get(Session(0)).Data!)["percentage"]!.GetValue<int>());
            job!.Finish(true,@"C:\safe\result.xlsx");
        }
        var saved=Assert.IsType<JsonObject>(manager.Get(Session(0)).Data);
        Assert.True(saved["completed"]!.GetValue<bool>());
        Assert.Equal("completed",saved["status"]!.GetValue<string>());
        Assert.Equal(100,saved["percentage"]!.GetValue<int>());
        Assert.Equal(@"C:\safe\result.xlsx",saved["filePath"]!.GetValue<string>());
        Assert.Equal(BridgeErrorCodes.MissingData,manager.Cancel(Session(0)).ErrorCode);
    }

    [Fact]
    public void FailedOrRevokedRun_NeverLeavesAnApparentSuccess()
    {
        using var source=new CancellationTokenSource();
        var manager=new ExportJobCoordinator();
        Assert.True(manager.TryBegin(Session(0),"EXPORT-C","monthly",source.Token,out var job));
        using(job!)
        {
            job!.Report(5,10);
            source.Cancel();
            job!.Finish(false);
        }
        var outcome=Assert.IsType<JsonObject>(manager.Get(Session(0)).Data);
        Assert.Equal("canceled",outcome["status"]!.GetValue<string>());
        Assert.False(outcome["completed"]!.GetValue<bool>());
        Assert.Null(outcome["filePath"]);
    }

    [Fact]
    public void OnlyOneActiveJobPerSession_OldRunCannotOverwriteNewerJob()
    {
        var manager=new ExportJobCoordinator();
        Assert.True(manager.TryBegin(Session(0),"EXPORT-ONE","monthly",default,out var first));
        using(first!)
        {
            Assert.False(manager.TryBegin(Session(0),"EXPORT-TWO","monthly",default,out _));
            first!.Finish(false);
        }
        Assert.True(manager.TryBegin(Session(0),"EXPORT-TWO","monthly",default,out var second));
        using(second!)
        {
            second!.Report(4,4);
            first!.Finish(true,"forged.xlsx"); // stale object must not change visible run
            Assert.Equal("EXPORT-TWO",((JsonObject)manager.Get(Session(0)).Data!)["jobId"]!.GetValue<string>());
            second!.Finish(true,"real.xlsx");
        }
        Assert.Equal("real.xlsx",((JsonObject)manager.Get(Session(0)).Data!)["filePath"]!.GetValue<string>());
    }

    [Fact]
    public void ProgressIsClampedAndRemainsUnknownUntilRealTotalKnown()
    {
        var manager=new ExportJobCoordinator();
        Assert.True(manager.TryBegin(Session(0),"EXPORT-CLAMP","monthly",default,out var job));
        using(job!)
        {
            var initial=Assert.IsType<JsonObject>(manager.Get(Session(0)).Data);
            Assert.Null(initial["percentage"]);
            job!.Report(200,100);
            var bounded=Assert.IsType<JsonObject>(manager.Get(Session(0)).Data);
            Assert.Equal(100,bounded["processed"]!.GetValue<int>());
            Assert.Equal(99,bounded["percentage"]!.GetValue<int>());
            job!.Finish(false);
        }
    }
}

/// <summary>Dispatcher cannot be bypassed by spoofing role or session in export job requests.</summary>
public sealed class ExportJobBridgeAuthorizationTests : IAsyncLifetime, IDisposable
{
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db=null!;
    private WebView2BridgeDispatcher _dispatcher=null!;
    private const string Nonce="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly BridgePrincipal Supervisor=new("TST-EXPORT-JOBS","test-user",
        UserRole.Supervisor,Nonce);

    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        _dispatcher=BridgeHandlerCatalog.Create(new BridgeSharedServices(_db));
    }

    private Task<BridgeDispatchResult> Call(string action,string nonce)
    {
        var request=new JsonObject
        {
            ["requestId"]=Guid.NewGuid().ToString("N"),
            ["protocolVersion"]=BridgeProtocol.ProtocolVersion,
            ["action"]=action,
            ["sessionNonce"]=nonce,
            ["payload"]=new JsonObject { ["role"]="GeneralAdministration" }
        };
        return _dispatcher.DispatchAsync(request.ToJsonString(),LocalWebUiOrigin.BaseUrl,true,
            n=>Task.FromResult<BridgePrincipal?>(n==Nonce?Supervisor:null));
    }

    [Theory]
    [InlineData("GetExportProgress")]
    [InlineData("CancelExport")]
    public async Task AuthenticatedRequestReportsNoJob_UnauthenticatedRequestIsDenied(string action)
    {
        var anonymous=await Call(action,"ffffffffffffffffffffffffffffffff");
        Assert.Equal(BridgeOutcomeKind.Unauthorized,anonymous.Kind);
        var current=await Call(action,Nonce);
        Assert.False(current.Response.Success);
        Assert.Equal(BridgeErrorCodes.MissingData,current.Response.ErrorCode);
    }
    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose(){_db?.Dispose();SyntheticFixtures.DeleteTempDatabase(_path);}
}
