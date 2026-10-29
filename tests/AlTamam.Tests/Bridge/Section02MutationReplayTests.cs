using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Section 02.14: repeatable synthetic SQLCipher mutation receipts at a stable
/// PersonId/SiteId/StatusId; no production data and no historic rewriting.
/// </summary>
public sealed class Section02MutationReplayTests : IAsyncLifetime, IDisposable
{
    private readonly string _path = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;
    private static readonly BridgePrincipal Actor = new(
        "TST-S02-ADMIN", "s02-administrator", UserRole.GeneralAdministration,
        "aabbccddeeff00112233445566778899");

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_path, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
        using var c = _db.CreateOpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Sites(SiteId,VisibleName,NormalizedName,CreatedAtUtc,RowVersion)
            VALUES('SITE-S02','موقع قديم للاختبار','موقع قديم للاختبار','2026-01-01T00:00:00Z',1);
            INSERT INTO SiteNameHistory(SiteId,VisibleNameSnapshot,ValidFromLocalDate,
                ChangedByUserId,RecordedAtUtc)
            VALUES('SITE-S02','موقع قديم للاختبار','2026-01-01',
                'TST-S02-ADMIN','2026-01-01T00:00:00Z');
            INSERT INTO AttendanceStatusDefinitions(StatusId,VisibleName,NormalizedName,
                ShortSymbol,ChosenColorHex,CreatedAtUtc,RowVersion)
            VALUES('STS-S02','حالة اختبار قديمة','حالة اختبار قديمة',
                'تس','#AABBCC','2026-01-01T00:00:00Z',1);
            INSERT INTO AttendanceStatusNameHistory(StatusId,VisibleNameSnapshot,
                ShortSymbolSnapshot,ColorSnapshot,ValidFromLocalDate,RecordedAtUtc)
            VALUES('STS-S02','حالة اختبار قديمة','تس','#AABBCC',
                '2026-01-01','2026-01-01T00:00:00Z');
            INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                CurrentServiceNumberA,NormalizedServiceNumberA,
                FirstRecordedLocalDate,FirstRecordedAtUtc,RowVersion)
            VALUES('PRN-S02','فرد تاريخي للاختبار','فرد تاريخي للاختبار',
                '0009','0009','2026-01-01','2026-01-01T00:00:00Z',1);
            INSERT INTO PersonIdentityHistory(PersonId,NameSnapshot,ServiceNumberASnapshot,
                ValidFromLocalDate,ChangedByUserId,RecordedAtUtc,Reason)
            VALUES('PRN-S02','فرد تاريخي للاختبار','0009','2026-01-01',
                'TST-S02-ADMIN','2026-01-01T00:00:00Z','سجل تجريبي');
            INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,
                ValidFromLocalDate,CreatedAtUtc)
            VALUES('ASG-S02','PRN-S02','SITE-S02',
                '2026-01-01','2026-01-01T00:00:00Z');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private long Count(string sql)
    {
        using var c = _db.CreateOpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task RenameSite_MissingOperationId_IsRejectedWithoutAnyHistoricalOrAuditWrite()
    {
        var svc = new AdminConfigurationService(_db);
        var payload = new JsonObject {
            ["siteId"]="SITE-S02",["name"]="محاولة تعديل بلا معرف",
            ["expectedRevision"]=1L
        };
        var response = await svc.RenameSiteAsync(payload,Actor,CancellationToken.None);
        Assert.False(response.Success);
        Assert.Equal(BridgeErrorCodes.ValidationError,response.ErrorCode);
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM SiteNameHistory WHERE SiteId='SITE-S02'"));
        Assert.Equal(0L,Count("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='SITE_RENAME'"));
        Assert.Equal(0L,Count("SELECT COUNT(*) FROM MutationReceipts"));
    }

    [Fact]
    public async Task RenameSite_LostResponseRetry_HasOneHistoricalVersionAndAudit()
    {
        var svc = new AdminConfigurationService(_db);
        var payload = new JsonObject{
            ["siteId"]="SITE-S02",["name"]="موقع مجدد للاختبار",
            ["expectedRevision"]=1L,["operationId"]="section02-site-rename-one"
        };
        var done = await svc.RenameSiteAsync(payload,Actor,CancellationToken.None);
        Assert.True(done.Success,done.UserMessage);
        var repeated = await svc.RenameSiteAsync((JsonObject)payload.DeepClone(),Actor,CancellationToken.None);
        Assert.True(repeated.Success,repeated.UserMessage);
        Assert.Equal(2L,Count("SELECT COUNT(*) FROM SiteNameHistory WHERE SiteId='SITE-S02'"));
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='SITE_RENAME'"));
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM MutationReceipts"));
        var conflicting = (JsonObject)payload.DeepClone();
        conflicting["name"]="موقع ثالث مختلف";
        var conflict = await svc.RenameSiteAsync(conflicting,Actor,CancellationToken.None);
        Assert.False(conflict.Success);
        Assert.Equal(BridgeErrorCodes.ConflictStale,conflict.ErrorCode);
    }

    [Fact]
    public async Task UpdateStatus_LostResponseRetry_HasOneHistoricalVersionAndAudit()
    {
        var svc = new AdminConfigurationService(_db);
        var payload = new JsonObject{
            ["statusId"]="STS-S02",["name"]="حالة اختبار جديدة",
            ["symbol"]="جد",["color"]="#ABCDEF",
            ["expectedRevision"]=1L,["operationId"]="section02-status-edit-one"
        };
        var done = await svc.UpdateStatusAsync(payload,Actor,CancellationToken.None);
        Assert.True(done.Success,done.UserMessage);
        var repeated = await svc.UpdateStatusAsync((JsonObject)payload.DeepClone(),Actor,CancellationToken.None);
        Assert.True(repeated.Success,repeated.UserMessage);
        Assert.Equal(2L,Count("SELECT COUNT(*) FROM AttendanceStatusNameHistory WHERE StatusId='STS-S02'"));
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='STATUS_UPDATE'"));
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM MutationReceipts"));
    }

    [Fact]
    public async Task DeactivateStatus_LostResponseRetry_IsOneSoftDeleteAndAudit()
    {
        var svc = new AdminConfigurationService(_db);
        var payload = new JsonObject{
            ["statusId"]="STS-S02",["expectedRevision"]=1L,
            ["operationId"]="section02-status-deactivate-one"
        };
        var done = await svc.DeactivateStatusAsync(payload,Actor,CancellationToken.None);
        Assert.True(done.Success,done.UserMessage);
        var repeated = await svc.DeactivateStatusAsync((JsonObject)payload.DeepClone(),Actor,CancellationToken.None);
        Assert.True(repeated.Success,repeated.UserMessage);
        Assert.Equal(0L,Count("SELECT ActiveFlag FROM AttendanceStatusDefinitions WHERE StatusId='STS-S02'"));
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='STATUS_DEACTIVATE'"));
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM MutationReceipts"));
    }

    [Fact]
    public async Task IdentityUpdate_LostResponseRetry_RetainsOldIdentityAndPermanentPersonId()
    {
        var svc = new PersonRosterService(_db);
        var payload = new JsonObject{
            ["personId"]="PRN-S02",["visibleName"]="فرد جديد الاسم للاختبار",
            ["serviceNumberA"]="۰۰۱۰",["baseRevision"]=1L,["expectedRevision"]=1L,
            ["effectiveLocalDate"]=DateTime.Today.ToString("yyyy-MM-dd"),
            ["reason"]="تصحيح تاريخي موثق",["operationId"]="section02-person-identity-one"
        };
        var done = await svc.UpdatePersonIdentityAsync(Actor.UserId,Actor.Username,payload,CancellationToken.None);
        Assert.True(done.Success,done.UserMessage);
        var repeated = await svc.UpdatePersonIdentityAsync(Actor.UserId,Actor.Username,
            (JsonObject)payload.DeepClone(),CancellationToken.None);
        Assert.True(repeated.Success,repeated.UserMessage);
        Assert.Equal(2L,Count("SELECT COUNT(*) FROM PersonIdentityHistory WHERE PersonId='PRN-S02'"));
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM AuditEvents WHERE OperationCode='PERSON_IDENTITY_UPDATE'"));
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM MutationReceipts"));
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM Persons WHERE PersonId='PRN-S02' AND CurrentServiceNumberA='0010'"));
        Assert.Equal(1L,Count("SELECT COUNT(*) FROM PersonIdentityHistory WHERE PersonId='PRN-S02' AND ServiceNumberASnapshot='0009'"));
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_path);
    }
}
