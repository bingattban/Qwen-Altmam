using System.Diagnostics;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace AlTamam.Tests.Performance;

/// <summary>
/// First measured 10k-person SSD/CI proxy, NOT a hardware-neutral speed guarantee.
/// Reports actual Windows runner specs/timings to test logs; correctness always gates CI.
/// </summary>
public sealed class TenThousandSearchDiagnostics : IAsyncLifetime, IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _path=SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db=null!;
    public TenThousandSearchDiagnostics(ITestOutputHelper output)=>_output=output;
    public async Task InitializeAsync()
    {
        _db=new AlTamamDbContext(_path,SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }
    public Task DisposeAsync()=>Task.CompletedTask;
    public void Dispose()
    {
        _db?.Dispose();SyntheticFixtures.DeleteTempDatabase(_path);
    }
    [Fact]
    public async Task SearchTenThousandPeople_ReportsActualRunnerMeasurementsAndCorrectResults()
    {
        const int size=10_000;
        using(var conn=_db.CreateOpenConnection())
        using(var tx=conn.BeginTransaction())
        using(var insert=conn.CreateCommand())
        {
            insert.Transaction=tx;
            insert.CommandText="""
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                    CurrentServiceNumberA,NormalizedServiceNumberA,
                    FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES($id,$name,$name,$number,$number,'2026-01-01','2026-01-01T00:00:00Z',1,1,1);
                """;
            var id=insert.Parameters.Add("$id",SqliteType.Text);
            var name=insert.Parameters.Add("$name",SqliteType.Text);
            var number=insert.Parameters.Add("$number",SqliteType.Text);
            for(int i=0;i<size;i++)
            {
                id.Value=$"TEST-P-{i:D6}";
                name.Value=$"اختبار بحث {i:D6}";
                number.Value=$"{i+1000:D5}";
                await insert.ExecuteNonQueryAsync();
            }
            tx.Commit();
        }
        var svc=new BridgeSharedServices(_db);
        await svc.SearchPersonsAsync("اختبار بحث 008000",25,default); // warm up
        var sw=Stopwatch.StartNew();
        var result=await svc.SearchPersonsAsync("اختبار بحث 008000",25,default);
        sw.Stop();
        Assert.True(result.Success,result.UserMessage);
        string perf=$"SECTION01_PERF personCount={size} elapsedMs={sw.Elapsed.TotalMilliseconds:F2} targetMs=200";
        string hardware=$"SECTION01_HW os={Environment.OSVersion} x64Process={Environment.Is64BitProcess} cpus={Environment.ProcessorCount} arch={System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} machine={Environment.MachineName}";
        _output.WriteLine(perf);
        _output.WriteLine(hardware);
        string? reportFile=Environment.GetEnvironmentVariable("ALTAMAM_PERF_REPORT");
        if(!string.IsNullOrWhiteSpace(reportFile))
        {
            string destination=Path.GetFullPath(reportFile);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination,perf+Environment.NewLine+hardware+Environment.NewLine);
        }
        // No CI hardware-specific hard time threshold: the requirement says target, not guarantee.
        Assert.Contains("TEST-P-008000",result.Data!.ToString());
    }
}
