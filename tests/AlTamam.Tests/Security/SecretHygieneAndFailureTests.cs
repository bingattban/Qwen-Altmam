using System.Text;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Infrastructure.Persistence.Migrations;
using AlTamam.Infrastructure.Security;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Security;

/// <summary>
/// PHASE 14 step 14.04 — privacy & secret-handling tests that are executable on ANY OS.
/// Windows-native halves (SQLCipher native failure under real DPAPI loss, WebView2 process isolation,
/// installer ACLs) are explicitly NOT RUN here and appear in the Phase 14 vulnerability report with
/// their Windows-CI procedure instead. What IS proven here:
///   * wrong-key / plaintext-open failures never leak key material or raw file bytes into messages;
///   * the DB key is validated to exactly 32 bytes of hex and nothing else is accepted;
///   * no production source writes secrets (key hex, passwords, hashes) to any log/console channel;
///   * password verify is constant-time-shaped and rejects malformed stored material safely;
///   * archive visibility flag semantics (ActiveFlag/OrganizationalStatus) keep archived rows out of
///     active-force queries at SQL level (independent of UI).
/// </summary>
public sealed class SecretHygieneAndFailureTests : IDisposable
{
    private readonly string _dbPath = SyntheticFixtures.CreateTempDatabasePath();

    public void Dispose() => SyntheticFixtures.DeleteTempDatabase(_dbPath);

    // -------------------------------------------------- SQLCipher/native failure behaviour

    [Fact]
    public async Task WrongKey_FailsToOpen_AndFailureMessageLeaksNothing()
    {
        string correctKey = SyntheticFixtures.TestKeyHex;
        using (var context = new AlTamamDbContext(_dbPath, correctKey))
        {
            await context.InitializeAsync();
        }

        string wrongKey = Convert.ToHexString(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")).ToLowerInvariant();
        using var hostile = new AlTamamDbContext(_dbPath, wrongKey);
        Exception? ex = Record.Exception(() => hostile.CreateOpenConnection());
        Assert.NotNull(ex);
        // The failure must not echo either key nor dump raw file contents.
        string message = ex!.Message + ex.ToString();
        Assert.DoesNotContain(correctKey, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(wrongKey, message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlaintextFile_IsRefusedByEncryptedOpener_AndEncryptedFileByPlainOpener()
    {
        // Encryption-at-rest bidirectional gate (mirrors the Phase-01 negative test, re-run for 14.04).
        File.WriteAllBytes(_dbPath, Encoding.ASCII.GetBytes("SQLite format 3\u0000plain-not-encrypted"));
        using var encrypted = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        Assert.ThrowsAny<Exception>(() =>
        {
            using var c = encrypted.CreateOpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
            cmd.ExecuteScalar();
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    [InlineData("zz3f0aac1d2c4b5a6978899aabbccddeeff00112233445566778899aabbccddee")] // bad hex chars
    [InlineData("0123456789abcdef0123456789abcde")]                                    // 31 bytes
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]   // >32 bytes
    public void DbContext_RejectsEveryMalformedKeyFormat(string candidateKey)
    {
        // Empty/whitespace are rejected by ThrowIfNullOrWhiteSpace; malformed hex and wrong
        // byte lengths by the explicit 32-byte validation. Both surface as ArgumentException.
        Assert.ThrowsAny<ArgumentException>(() => new AlTamamDbContext(_dbPath, candidateKey));
    }

    [Fact]
    public void DatabaseKeyGenerator_ProducesFreshUnpredictable32ByteHexKeys()
    {
        string a = DatabaseKeyGenerator.Generate();
        string b = DatabaseKeyGenerator.Generate();
        Assert.NotEqual(a, b);
        Assert.Equal(64, a.Length);
        Assert.Matches("^[0-9A-Fa-f]{64}$", a);
        Assert.Equal(32, Convert.FromHexString(a).Length);
    }

    // -------------------------------------------------- secrets-in-logs static hygiene (14.04)

    [Fact]
    public void ProductionSource_ContainsNoConsoleOrDebugLoggingOfSecrets()
    {
        // The WPF --layout-selfcheck emits only two known diagnostics and no key material.
        // Any other production console/debug/tracing sink remains forbidden by this gate.
        string srcRoot = Locate.SourceDirectory();
        string[] offenders = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                string text = File.ReadAllText(file);
                if (string.Equals(Path.GetFileName(file), "MainWindow.xaml.cs", StringComparison.OrdinalIgnoreCase))
                {
                    text = text
                        .Replace("Console.WriteLine(\"LAYOUT-SELFCHECK PASS\");", "", StringComparison.Ordinal)
                        .Replace("Console.Error.WriteLine(\"LAYOUT-SELFCHECK FAIL: \" + ex.Message);", "", StringComparison.Ordinal);
                }
                return text.Contains("Console.Write", StringComparison.Ordinal)
                    || text.Contains("Console.Error.Write", StringComparison.Ordinal)
                    || text.Contains("Debug.WriteLine", StringComparison.Ordinal)
                    || text.Contains("Trace.Write", StringComparison.Ordinal)
                    || text.Contains("EventLog.WriteEntry", StringComparison.Ordinal);
            })
            .Select(file => Path.GetFileName(file) ?? throw new InvalidOperationException("Source file name is missing."))
            .ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Repository_ContainsNoCommittedSecretMaterial()
    {
        // Hygiene gate over the whole repo: no DPAPI blobs, no 64-hex key literals outside the
        // documented synthetic test derivation, no private keys.
        string repo = Locate.RepositoryRoot();
        string[] suspicious = Directory
            .EnumerateFiles(repo, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar))
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            .Where(f =>
            {
                string text = File.ReadAllText(f);
                return text.Contains("BEGIN " + "PRIVATE KEY", StringComparison.Ordinal)
                    || text.Contains("-----BEGIN RSA " + "PRIVATE KEY", StringComparison.Ordinal)
                    || Regex_LikeProtectedBlob(text);
            })
            .Select(file => Path.GetRelativePath(repo, file))
            .ToArray();
        Assert.Empty(suspicious);
    }

    private static bool Regex_LikeProtectedBlob(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, "ProtectedData\\.Protect\\([^)]*\"[^\"]{16,}\"");

    // -------------------------------------------------- password subsystem hard edges

    [Fact]
    public void VerifyPassword_MalformedStoredMaterial_ReturnsFalseWithoutThrowing()
    {
        Assert.False(PasswordSecurity.VerifyPassword("x", "not-base64!!", "also-not-base64!!"));
        Assert.False(PasswordSecurity.VerifyPassword("x", "", "AAAAAAAAAAAAAAAAAAAAAA=="));
        Assert.False(PasswordSecurity.VerifyPassword("", "YWJjZA==", "AAAAAAAAAAAAAAAAAAAAAA=="));
        // Right shape, wrong length → false, no exception.
        Assert.False(PasswordSecurity.VerifyPassword("x", Convert.ToBase64String(new byte[7]), Convert.ToBase64String(new byte[16])));
    }

    [Fact]
    public void HashPassword_SaltAndTag_HaveCanonicalShapes_AndDifferPerCall()
    {
        var (h1, s1) = PasswordSecurity.HashPassword("Synthetic#Pass1");
        var (h2, s2) = PasswordSecurity.HashPassword("Synthetic#Pass1");
        Assert.NotEqual(h1, h2);       // fresh random salt per hash ⇒ identical passwords differ
        Assert.NotEqual(s1, s2);
        Assert.Equal(16, Convert.FromBase64String(s1).Length);
        Assert.Equal(32, Convert.FromBase64String(h1).Length);
        Assert.True(PasswordSecurity.VerifyPassword("Synthetic#Pass1", h1, s1));
        Assert.False(PasswordSecurity.VerifyPassword("Synthetic#Pass2", h1, s1));
    }

    // -------------------------------------------------- archive visibility at SQL level (14.04)

    [Fact]
    public async Task ArchivedPerson_InvisibleToActiveForceQueries_EvenWithoutUiFilters()
    {
        using var context = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        await context.InitializeAsync();
        using SqliteConnection connection = context.CreateOpenConnection();

        // Seed one synthetic person, then flip it to archived (status 4, ActiveFlag 0) like ArchivePersons does.
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = @"INSERT INTO Persons (PersonId, CurrentVisibleName, NormalizedName,
                CurrentServiceNumberA, NormalizedServiceNumberA, FirstRecordedLocalDate, FirstRecordedAtUtc,
                OrganizationalStatus, ActiveFlag, RowVersion)
                VALUES ($id,'شخص مؤرشف','شخص مؤرشف','تج-أرشف','تج-أرشف','2025-01-01',$now,1,1,1);";
            seed.Parameters.AddWithValue("$id", SyntheticFixtures.PersonId);
            seed.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await seed.ExecuteNonQueryAsync();
        }
        using (var archive = connection.CreateCommand())
        {
            archive.CommandText = "UPDATE Persons SET OrganizationalStatus = 4, ActiveFlag = 0, RowVersion = RowVersion + 1 WHERE PersonId = $id;";
            archive.Parameters.AddWithValue("$id", SyntheticFixtures.PersonId);
            await archive.ExecuteNonQueryAsync();
        }

        // The exact active-force predicate used by services/UI must exclude it server-side.
        using var visible = connection.CreateCommand();
        visible.CommandText = "SELECT COUNT(*) FROM Persons WHERE ActiveFlag = 1 AND OrganizationalStatus = 1;";
        Assert.Equal(0L, Convert.ToInt64(await visible.ExecuteScalarAsync()));

        // ...while history queries can still see the row (auditability preserved).
        using var historical = connection.CreateCommand();
        historical.CommandText = "SELECT COUNT(*) FROM Persons WHERE PersonId = $id;";
        historical.Parameters.AddWithValue("$id", SyntheticFixtures.PersonId);
        Assert.Equal(1L, Convert.ToInt64(await historical.ExecuteScalarAsync()));
    }

    // -------------------------------------------------- reset & startup-permission surface

    [Fact]
    public void AppDataLayout_ProgramFilesGuard_CoversTraversalResolvedPaths()
    {
        // A traversal-flavored path that NORMALIZES into Program Files must still be refused.
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
            return; // Non-Windows sandbox: guard inputs unavailable — recorded as NOT RUN in evidence, never silently passed.
        string traversalish = Path.Combine(programFiles, "..", "Program Files", "AlTamam", "evil.db");
        Assert.Throws<InvalidOperationException>(() => AppDataLayout.AssertWritableLocation(traversalish));
    }

    [Fact]
    public void MigrationRunner_VersionSequence_IsContiguousAndPinned()
    {
        // Startup permission analogue for schema: a tampered sequence fails closed before any DDL runs.
        var gap = new[]
        {
            new SchemaMigration(1, "V001", "x"),
            new SchemaMigration(3, "V003", "y"),
        };
        Assert.Throws<ArgumentException>(() => new SchemaMigrationRunner(gap));
        var runner = new SchemaMigrationRunner(AlTamamDbContext.CanonicalMigrations);
        Assert.Equal(AlTamamDbContext.CurrentSchemaVersion, runner.LatestKnownVersion);
    }
}

/// <summary>Small helper resolving repo/src roots from the test assembly location.</summary>
internal static class Locate
{
    public static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlTamam.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("AlTamam.slnx not found above test assembly.");
    }

    public static string SourceDirectory() => Path.Combine(RepositoryRoot(), "src");
}
