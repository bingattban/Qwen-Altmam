using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace AlTamam.Tests.Performance;

/// <summary>
/// Phase 15 extended SQLCipher performance DIAGNOSTIC, not a 10k-row Windows GUI
/// acceptance or a hardware-neutral performance guarantee. All 10,000 active
/// synthetic people have two years of monthly history plus 31 saved daily
/// attendance statuses in a third year (550,000 encrypted attendance rows).
/// A real production MonthlyGridAsync page and independently computed daily
/// aggregates must match the seeded truth. Timing is measured and recorded,
/// NEVER asserted against an arbitrary CI-machine latency target.
/// </summary>
public sealed class Phase15TenThousandMultiYearDiagnostics : IAsyncLifetime, IDisposable
{
    private const int Personnel = 10_000;
    private const int HistoricMonths = 24;
    private const int CurrentMonthDays = 31;
    private const int ExpectedAttendance = Personnel * (HistoricMonths + CurrentMonthDays);

    private readonly ITestOutputHelper _output;
    private readonly string _dbPath = SyntheticFixtures.CreateTempDatabasePath();
    private AlTamamDbContext _db = null!;

    public Phase15TenThousandMultiYearDiagnostics(ITestOutputHelper output)
        => _output = output;

    public async Task InitializeAsync()
    {
        _db = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        await _db.InitializeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _db?.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_dbPath);
    }

    [Fact]
    public async Task TenThousandPeople_ThreeCalendarYears_ThirtyOneDailyColumns_ExactHistoricalCounts()
    {
        var seedWatch = Stopwatch.StartNew();
        using (var connection = _db.CreateOpenConnection())
        using (var transaction = connection.BeginTransaction())
        using (var person = connection.CreateCommand())
        using (var assignment = connection.CreateCommand())
        using (var attendance = connection.CreateCommand())
        {
            person.Transaction = transaction;
            assignment.Transaction = transaction;
            attendance.Transaction = transaction;
            person.CommandText = """
                INSERT INTO Persons(PersonId,CurrentVisibleName,NormalizedName,
                  CurrentServiceNumberA,NormalizedServiceNumberA,
                  FirstRecordedLocalDate,FirstRecordedAtUtc,OrganizationalStatus,ActiveFlag,RowVersion)
                VALUES($pid,$name,$name,$sn,$sn,'2024-01-01','2024-01-01T00:00:00Z',1,1,1);
                """;
            assignment.CommandText = """
                INSERT INTO PersonAssignments(AssignmentId,PersonId,SiteId,
                  ValidFromLocalDate,ValidToLocalDate,SourceMovementId,CreatedAtUtc)
                VALUES($aid,$pid,$site,'2024-01-01',NULL,NULL,'2024-01-01T00:00:00Z');
                """;
            attendance.CommandText = """
                INSERT INTO AttendanceRecords(PersonId,AttendanceLocalDate,
                  StatusId,StatusNameSnapshot,StatusSymbolSnapshot,StatusColorSnapshot,
                  AssignmentIdAtEffectiveDate,SiteIdAtEffectiveDate,SiteNameSnapshot,
                  Source,CreatedByUserId,CommittedAtUtc,RevisionNumber,IdempotencyKey)
                VALUES($pid,$date,$status,$statusName,$symbol,$color,
                  $aid,$site,'موقع تجريبي','manual','TST-USER-GA','2026-10-31T16:00:00Z',1,$key);
                """;
            var pid = person.Parameters.Add("$pid", SqliteType.Text);
            var name = person.Parameters.Add("$name", SqliteType.Text);
            var number = person.Parameters.Add("$sn", SqliteType.Text);
            var aid = assignment.Parameters.Add("$aid", SqliteType.Text);
            var assignmentPerson = assignment.Parameters.Add("$pid", SqliteType.Text);
            var assignmentSite = assignment.Parameters.Add("$site", SqliteType.Text);
            var recordPerson = attendance.Parameters.Add("$pid", SqliteType.Text);
            var recordDate = attendance.Parameters.Add("$date", SqliteType.Text);
            var status = attendance.Parameters.Add("$status", SqliteType.Text);
            var statusName = attendance.Parameters.Add("$statusName", SqliteType.Text);
            var symbol = attendance.Parameters.Add("$symbol", SqliteType.Text);
            var color = attendance.Parameters.Add("$color", SqliteType.Text);
            var recordAid = attendance.Parameters.Add("$aid", SqliteType.Text);
            var recordSite = attendance.Parameters.Add("$site", SqliteType.Text);
            var recordKey = attendance.Parameters.Add("$key", SqliteType.Text);

            person.Prepare();
            assignment.Prepare();
            attendance.Prepare();

            for (int i = 1; i <= Personnel; i++)
            {
                string identity = $"P15-SYN-{i:D6}";
                string assignmentId = $"P15-A-{i:D6}";
                pid.Value = identity;
                name.Value = $"فرد اختباري {i:D6}";
                number.Value = $"{100000 + i}";
                person.ExecuteNonQuery();

                aid.Value = assignmentId;
                assignmentPerson.Value = identity;
                assignmentSite.Value = SyntheticFixtures.SiteId;
                assignment.ExecuteNonQuery();

                recordPerson.Value = identity;
                recordAid.Value = assignmentId;
                recordSite.Value = SyntheticFixtures.SiteId;
                bool present = i % 5 != 0;
                status.Value = present ? "STS-PRESENT" : "STS-ABSENT";
                statusName.Value = present ? "موجود" : "غياب";
                symbol.Value = present ? "م" : "غ";
                color.Value = present ? "#38BDF8" : "#EF4444";

                for (int year = 2024; year <= 2025; year++)
                for (int month = 1; month <= 12; month++)
                {
                    string date = $"{year}-{month:D2}-01";
                    recordDate.Value = date;
                    recordKey.Value = $"P15:{identity}:{date}";
                    attendance.ExecuteNonQuery();
                }
                for (int day = 1; day <= CurrentMonthDays; day++)
                {
                    string date = $"2026-10-{day:D2}";
                    recordDate.Value = date;
                    recordKey.Value = $"P15:{identity}:{date}";
                    attendance.ExecuteNonQuery();
                }
            }
            transaction.Commit();
        }
        seedWatch.Stop();

        using var read = _db.CreateOpenConnection();
        static long Count(SqliteConnection connection, string sql)
        {
            using var q = connection.CreateCommand();
            q.CommandText = sql;
            return Convert.ToInt64(q.ExecuteScalar());
        }
        Assert.Equal(Personnel, Count(read,
            "SELECT COUNT(*) FROM Persons WHERE ActiveFlag=1;"));
        Assert.Equal(ExpectedAttendance, Count(read,
            "SELECT COUNT(*) FROM AttendanceRecords;"));
        Assert.Equal(Personnel * HistoricMonths, Count(read,
            "SELECT COUNT(*) FROM AttendanceRecords WHERE AttendanceLocalDate<'2026-01-01';"));
        Assert.Equal(3L, Count(read,
            "SELECT COUNT(DISTINCT substr(AttendanceLocalDate,1,4)) FROM AttendanceRecords;"));

        var groupWatch = Stopwatch.StartNew();
        int observedDays = 0;
        using (var group = read.CreateCommand())
        {
            group.CommandText = """
                SELECT AttendanceLocalDate, COUNT(*)
                  FROM AttendanceRecords
                 WHERE AttendanceLocalDate >= '2026-10-01'
                   AND AttendanceLocalDate < '2026-11-01'
                 GROUP BY AttendanceLocalDate ORDER BY AttendanceLocalDate;
                """;
            using var reader = group.ExecuteReader();
            while (reader.Read())
            {
                Assert.Equal(Personnel, reader.GetInt64(1));
                observedDays++;
            }
        }
        groupWatch.Stop();
        Assert.Equal(CurrentMonthDays, observedDays);

        var uiQueryWatch = Stopwatch.StartNew();
        var production = await new DailyMonthlyReadService(_db).MonthlyGridAsync(
            new JsonObject {
                ["year"]=2026, ["month"]=10, ["dayFrom"]=1, ["dayTo"]=31,
                ["page"]=1, ["pageSize"]=100, ["archiveMode"]=true
            }, CancellationToken.None);
        uiQueryWatch.Stop();
        Assert.True(production.Success, production.UserMessage);
        var payload = Assert.IsType<JsonObject>(production.Data);
        var rows = Assert.IsType<JsonArray>(payload["items"]);
        Assert.Equal(100, rows.Count);
        Assert.Equal(31, payload["daysInMonth"]!.GetValue<int>());
        var first = Assert.IsType<JsonObject>(rows[0]);
        var savedDays = Assert.IsType<JsonObject>(first["days"]);
        Assert.Equal(CurrentMonthDays, savedDays.Count);
        Assert.Equal(Personnel.ToString(), payload["total"]!.ToString());

        // Measure an actual native XLSX on the SAME encrypted SQLCipher fixture.
        // Use the shipped streaming monthly exporter rather than a synthetic XML
        // writer or a mock. Its 100-person pages must cover all 10,000 identities.
        // This remains a runner-specific SERVICE benchmark, not full WPF GUI UAT.
        string exportFolder = Path.Combine(Path.GetTempPath(),
            "altamam-phase15-monthly-excel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportFolder);
        double excelElapsedMs;
        long excelFileBytes;
        string excelFileSha256;
        int excelWorksheetRows = 0;
        try
        {
            var exportRequest = new JsonObject
            {
                ["year"] = 2026, ["month"] = 10,
                ["siteId"] = SyntheticFixtures.SiteId,
                ["archiveMode"] = true,
                ["consolidatedOnly"] = true,
                ["directory"] = exportFolder,
                ["fileName"] = "Phase15_10000_Monthly.xlsx"
            };
            var excelWatch = Stopwatch.StartNew();
            var export = await new MonthlyExcelExportService(_db).ExportAsync(
                exportRequest, CancellationToken.None);
            excelWatch.Stop();
            Assert.True(export.Success, export.UserMessage);
            excelElapsedMs = Math.Round(excelWatch.Elapsed.TotalMilliseconds, 2);
            var exportReceipt = Assert.IsType<JsonObject>(export.Data);
            Assert.Equal((long)Personnel, exportReceipt["personCount"]!.GetValue<long>());
            string workbookPath = exportReceipt["filePath"]!.GetValue<string>();
            Assert.True(File.Exists(workbookPath));
            Assert.Equal(exportFolder, Path.GetDirectoryName(workbookPath));
            Assert.Empty(Directory.GetFiles(exportFolder, "*.part"));
            using (var workbook = ZipFile.OpenRead(workbookPath))
            {
                var sheet = workbook.GetEntry("xl/worksheets/sheet1.xml");
                Assert.NotNull(sheet);
                using var stream = sheet!.Open();
                using var xml = XmlReader.Create(stream,
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
                const string spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                XNamespace ns = spreadsheet;
                while (xml.ReadToFollowing("row", spreadsheet))
                {
                    excelWorksheetRows++;
                    using var subtree = xml.ReadSubtree();
                    var actualRow = XElement.Load(subtree);
                    Assert.Equal(excelWorksheetRows,
                        int.Parse(actualRow.Attribute("r")!.Value,
                            System.Globalization.CultureInfo.InvariantCulture));
                    Assert.Empty(actualRow.Descendants(ns + "f"));
                    if (excelWorksheetRows <= 3) continue;
                    // Full census of the historical snapshot and all 31 days,
                    // independent of the SQL reader's own result collection.
                    int personOrdinal = excelWorksheetRows - 3;
                    string[] cells = actualRow.Elements(ns + "c")
                        .Select(c => c.Element(ns + "is")?.Element(ns + "t")?.Value ?? "")
                        .ToArray();
                    Assert.Equal(35, cells.Length);
                    Assert.Equal(personOrdinal.ToString(
                        System.Globalization.CultureInfo.InvariantCulture), cells[0]);
                    Assert.Equal((100000 + personOrdinal).ToString(
                        System.Globalization.CultureInfo.InvariantCulture), cells[1]);
                    Assert.Equal($"فرد اختباري {personOrdinal:D6}", cells[2]);
                    Assert.Equal("غير محدد", cells[3]);
                    string statusName = personOrdinal % 5 == 0 ? "غياب" : "موجود";
                    for (int day = 1; day <= CurrentMonthDays; day++)
                        Assert.Equal(statusName, cells[day + 3]);
                }
            }
            Assert.Equal(Personnel + 3, excelWorksheetRows);
            excelFileBytes = new FileInfo(workbookPath).Length;
            Assert.True(excelFileBytes > 1_000, "Native 10k-person XLSX cannot be empty.");
            using (var exportedFile = File.OpenRead(workbookPath))
                excelFileSha256 = Convert.ToHexString(SHA256.HashData(exportedFile))
                    .ToLowerInvariant();
            Assert.Empty(Directory.GetFiles(exportFolder, "*.part"));
        }
        finally
        {
            // The benchmark never publishes synthetic personnel XLSX as a
            // customer file and must not leave it on the Windows CI runner.
            try { Directory.Delete(exportFolder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var report = new
        {
            schema = "AlTamam.Phase15.ThreeYearSqlCipherDiagnostics.v1",
            status = "MEASURED_SYNTHETIC_SQLCIPHER_NOT_REAL_10K_GUI_ACCEPTANCE",
            personsActive = Personnel,
            historyMonthsPerPerson = HistoricMonths,
            calendarYears = 3,
            fullMonthDaysPerPerson = CurrentMonthDays,
            attendanceRecords = ExpectedAttendance,
            measuredGroupedDailyRows = observedDays,
            productionMonthlyPageSize = rows.Count,
            seedElapsedMs = Math.Round(seedWatch.Elapsed.TotalMilliseconds, 2),
            seedRowsPerSecond = Math.Round(ExpectedAttendance /
                Math.Max(0.001, seedWatch.Elapsed.TotalSeconds), 2),
            grouped31DayElapsedMs = Math.Round(groupWatch.Elapsed.TotalMilliseconds, 2),
            productionMonthlyFirstPageElapsedMs = Math.Round(uiQueryWatch.Elapsed.TotalMilliseconds, 2),
            os = Environment.OSVersion.ToString(),
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            processorCount = Environment.ProcessorCount,
            availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            workingSetBytes = Environment.WorkingSet,
            actualCheckoutSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "NOT_RECORDED",
            sourceHeadSha = Environment.GetEnvironmentVariable("ALTAMAM_SOURCE_HEAD_SHA") ?? "NOT_RECORDED",
            githubRunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "NOT_RECORDED",
            fullGui10000People31DayEndToEnd = "NOT_EXECUTED",
            excelExportThroughput = "MEASURED_SYNTHETIC_10000_PERSON_XLSX_NOT_FULL_GUI_ACCEPTANCE",
            monthlyExcelExportPeople = Personnel,
            monthlyExcelXlsxWorksheetRows = excelWorksheetRows,
            monthlyExcelExportElapsedMs = excelElapsedMs,
            monthlyExcelXlsxBytes = excelFileBytes,
            monthlyExcelXlsxSha256 = excelFileSha256,
            monthlyExcelNoPartialFiles = true,
            physicalPowerFailure = "NOT_EXECUTED",
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        _output.WriteLine(json);
        string? target = Environment.GetEnvironmentVariable("ALTAMAM_PHASE15_MULTIYEAR_PERF_REPORT");
        if (!string.IsNullOrWhiteSpace(target))
        {
            string full = Path.GetFullPath(target);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, json + Environment.NewLine);
        }
    }
}
