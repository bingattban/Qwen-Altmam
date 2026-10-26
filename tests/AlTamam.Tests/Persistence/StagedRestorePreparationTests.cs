using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Persistence;

public sealed class StagedRestorePreparationTests : IDisposable
{
    private readonly string _folder=Path.Combine(Path.GetTempPath(),
        "altamam-restore-stage-"+Guid.NewGuid().ToString("N"));
    private string Current=>Path.Combine(_folder,"live.db");
    private string Incoming=>Path.Combine(_folder,"incoming.dbbackup");
    private const string Key = SyntheticFixtures.TestKeyHex;

    [Fact]
    public async Task Stage_RetainsCurrentPeopleInRescue_ButIncomingDifferentHistory_WithoutChangingLiveDb()
    {
        Directory.CreateDirectory(_folder);
        using var live=new AlTamamDbContext(Current,Key);
        await live.InitializeAsync();

        // Older encrypted backup has zero persons; the current DB has one.
        await live.CreateVerifiedEncryptedBackupAsync(Incoming);
        using(var c=live.CreateOpenConnection())
        using(var cmd=c.CreateCommand())
        {
            cmd.CommandText="""
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                    CurrentServiceNumberA,NormalizedServiceNumberA,
                    FirstRecordedLocalDate,FirstRecordedAtUtc,
                    OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES('TST-RESTORE-STAGING-P1','فرد حاضر','فرد حاضر',
                    '991','991','2026-10-26','2026-10-26T00:00:00Z',1,1,1);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var service=new EncryptedRestoreStagingService(live);
        var prepared=await service.PrepareAsync(Incoming);
        Assert.True(File.Exists(prepared.StageFilePath));
        Assert.True(File.Exists(prepared.RescueFilePath));
        Assert.NotEqual(prepared.StageFilePath,prepared.RescueFilePath);
        Assert.Equal(AlTamamDbContext.CurrentSchemaVersion,prepared.SchemaVersion);
        Assert.Equal(0L,prepared.SourcePeople);
        Assert.Equal(1L,prepared.CurrentPeople);
        var selected=await live.InspectEncryptedBackupAsync(prepared.StageFilePath);
        var rescue=await live.InspectEncryptedBackupAsync(prepared.RescueFilePath);
        Assert.Equal(prepared.StageSha256,selected.Sha256);
        Assert.Equal(prepared.RescueSha256,rescue.Sha256);
        Assert.Equal(0L,selected.PeopleCount);
        Assert.Equal(1L,rescue.PeopleCount);
        using var current=live.CreateOpenConnection();
        using var query=current.CreateCommand();
        query.CommandText="SELECT COUNT(*) FROM Persons;";
        Assert.Equal(1L,Convert.ToInt64(await query.ExecuteScalarAsync()));
        Assert.True(live.HasValidIntegrity());
    }

    [Fact]
    public async Task WrongKeyOrCorruptFile_RejectsBeforeRescueOrStage()
    {
        Directory.CreateDirectory(_folder);
        using var live=new AlTamamDbContext(Current,Key);
        await live.InitializeAsync();
        string other=Path.Combine(_folder,"different-key.db");
        using(var alien=new AlTamamDbContext(other,new string('A',64)))
        {
            await alien.InitializeAsync();
            await alien.CreateVerifiedEncryptedBackupAsync(Incoming);
        }
        var staging=new EncryptedRestoreStagingService(live);
        await Assert.ThrowsAnyAsync<Exception>(()=>staging.PrepareAsync(Incoming));
        Assert.False(Directory.Exists(Path.Combine(_folder,"backups")));
        Assert.Empty(Directory.GetFiles(_folder,".altamam-restore-stage-*"));
        Assert.True(live.HasValidIntegrity());
    }

    [Fact]
    public async Task InvalidSchemaVersion_RejectsBeforeAnyRecoveryFiles()
    {
        Directory.CreateDirectory(_folder);
        using var current=new AlTamamDbContext(Current,Key);
        await current.InitializeAsync();
        string legacy=Path.Combine(_folder,"legacy.db");
        using(var db=new AlTamamDbContext(legacy,Key))
        using(var c=db.CreateOpenConnection())
            await new AlTamam.Infrastructure.Persistence.Migrations.SchemaMigrationRunner(
                [AlTamamDbContext.CanonicalMigrations[0]]).UpgradeAsync(c);
        using(var db=new AlTamamDbContext(legacy,Key))
            await db.CreateVerifiedEncryptedBackupAsync(Incoming);
        var stage=new EncryptedRestoreStagingService(current);
        await Assert.ThrowsAsync<InvalidDataException>(()=>stage.PrepareAsync(Incoming));
        Assert.False(Directory.Exists(Path.Combine(_folder,"backups")));
        Assert.True(current.HasValidIntegrity());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try{if(Directory.Exists(_folder))Directory.Delete(_folder,true);}
        catch(IOException){ /* synthetic native Windows SQLite handles */ }
        catch(UnauthorizedAccessException){ /* synthetic native Windows SQLite handles */ }
    }
}
