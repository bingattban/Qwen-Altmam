using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>Never run on a real database. Uses throwaway SQLCipher fixtures.</summary>
public sealed class Section03FullResetTests : IAsyncLifetime, IDisposable
{
    private readonly string _file=SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db=null!;
    private BridgeSharedServices _auth=null!;
    private BridgePrincipal _admin=null!;
    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_file,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        _auth=new BridgeSharedServices(_db);
        var founded=await _auth.ProvisionFirstAdministratorAsync(
            "resetOwner","test-password-123",CancellationToken.None);
        Assert.True(founded.Success,founded.UserMessage);
        _admin=new BridgePrincipal(Assert.IsType<JsonObject>(founded.Data)["userId"]!.GetValue<string>(),
            "resetOwner",UserRole.GeneralAdministration,"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    }
    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_file);
    }
    private long Count(string table)
    {
        using var conn=_db.CreateOpenConnection();
        using var query=conn.CreateCommand();
        query.CommandText=$"SELECT COUNT(*) FROM \"{table}\";";
        return Convert.ToInt64(query.ExecuteScalar());
    }
    private JsonObject CommitArgs(JsonObject preview,string password="test-password-123")=>new()
    {
        ["previewToken"]=preview["previewToken"]!.GetValue<string>(),
        ["expectedRevision"]=preview["expectedRevision"]!.GetValue<long>(),
        ["password"]=password,
        ["confirmation"]="تصفير جميع البيانات"
    };
    [Fact]
    public async Task OnlyGeneralAdminMayPreviewOrCommit_AndPasswordIsRequired()
    {
        var svc=new FullDataResetService(_db);
        var supervisor=_admin with {Role=UserRole.Supervisor};
        Assert.False((await svc.PreviewAsync(supervisor,default)).Success);
        Assert.False((await svc.CommitAsync(new JsonObject(),supervisor,default)).Success);
        var preview=Assert.IsType<JsonObject>((await svc.PreviewAsync(_admin,default)).Data);
        var wrong=await svc.CommitAsync(CommitArgs(preview,"wrong"),_admin,default);
        Assert.False(wrong.Success);
        Assert.Equal(1,Count("Users"));
        Assert.Equal(1,Count("AuditEvents"));
    }
    [Fact]
    public async Task Commit_ResetsAllRowsAndRebuildsProtectedSeeds_WithoutAlteringSchema()
    {
        var svc=new FullDataResetService(_db);
        var preview=Assert.IsType<JsonObject>((await svc.PreviewAsync(_admin,default)).Data);
        var args=CommitArgs(preview);
        var response=await svc.CommitAsync(args,_admin,default);
        Assert.True(response.Success,response.UserMessage);
        Assert.Equal(0,Count("Users"));
        Assert.Equal(0,Count("AuditEvents"));
        Assert.Equal(0,Count("UserSessions"));
        Assert.Equal(0,Count("PreviewTokens"));
        Assert.Equal(1,Count("Sites"));
        Assert.Equal(4,Count("AttendanceStatusDefinitions"));
        Assert.Equal(4,Count("CustomFieldDefinitions"));
        Assert.Equal(AlTamamDbContext.CurrentSchemaVersion,Count("SchemaVersions"));
        Assert.True(_db.HasValidIntegrity());
        Assert.False(await _auth.IsProvisionedAsync());
        Assert.False((await svc.CommitAsync(args,_admin,default)).Success);
        var newOwner=await _auth.ProvisionFirstAdministratorAsync(
            "newOwner","test-password-123",default);
        Assert.True(newOwner.Success,newOwner.UserMessage);
    }
    [Fact]
    public async Task StalePreview_IsRejectedWithoutChangingPersonsOrAudit()
    {
        var svc=new FullDataResetService(_db);
        var preview=Assert.IsType<JsonObject>((await svc.PreviewAsync(_admin,default)).Data);
        var founded=await new UserAccountManagementService(_db).ListAsync(
            new JsonObject(),_admin,default);
        Assert.True(founded.Success);
        // Introduce an authorized new mutation after the preview.
        await _db.ExecuteInWriteTransactionAsync(async(c,t)=>
        {
            using var cmd=c.CreateCommand();cmd.Transaction=t;
            cmd.CommandText="INSERT INTO FailedLoginEvents(UsernameAttempted,AttemptedAtUtc,FailureReason) VALUES('a','2026-10-24T00:00:00Z','test');";
            await cmd.ExecuteNonQueryAsync();return 0;
        });
        var outcome=await svc.CommitAsync(CommitArgs(preview),_admin,default);
        Assert.False(outcome.Success);
        Assert.Equal(1,Count("Users"));
        Assert.Equal(1,Count("AuditEvents"));
        Assert.Equal(1,Count("FailedLoginEvents"));
    }
}
