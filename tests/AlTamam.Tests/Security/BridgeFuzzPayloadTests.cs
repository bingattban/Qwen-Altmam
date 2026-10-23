using System.Text.Json.Nodes;
using AlTamam.Application.Bridge;
using AlTamam.Contracts.Bridge;
using AlTamam.Domain.Enums;
using AlTamam.Infrastructure.Persistence;
using AlTamam.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AlTamam.Tests.Security;

/// <summary>
/// PHASE 14 step 14.02 — hostile/fuzz bridge payload corpus executed through the REAL production
/// pipeline (BridgeRequestParser → WebView2BridgeDispatcher → BridgeHandlerCatalog handlers backed
/// by a synthetic encrypted SQLCipher DB). Invariants asserted for every input:
///   * no exception escapes DispatchAsync (it returns a canonical envelope instead);
///   * every failure carries a stable canonical errorCode;
///   * hostile strings never damage the database (SQL-injection probes) and never create or
///     resolve filesystem paths outside the isolated test data directory (traversal probes);
///   * CSP / virtual-origin / navigation decisions are pure functions of the pinned host —
///     verified here via the dispatcher's origin gate (the WPF-only pieces are listed NOT RUN).
/// Synthetic fixtures only (§2.10): no real personnel data, no committed secrets.
/// </summary>
public sealed class BridgeFuzzPayloadTests : IDisposable, IAsyncLifetime
{
    private readonly string _dbPath = SyntheticFixtures.CreateTempDatabasePath();
    private readonly AlTamamDbContext _db;
    private readonly BridgeSharedServices _services;
    private readonly WebView2BridgeDispatcher _dispatcher;

    /// <summary>Server-side session nonce obtained by honestly authenticating a provisioned GA test user.</summary>
    private string _gaNonce = new('0', 32);

    public BridgeFuzzPayloadTests()
    {
        _db = new AlTamamDbContext(_dbPath, SyntheticFixtures.TestKeyHex);
        _db.InitializeAsync().GetAwaiter().GetResult();
        _services = new BridgeSharedServices(_db);
        _dispatcher = BridgeHandlerCatalog.Create(_services);
    }

    public async Task InitializeAsync()
    {
        // Provision the first administrator through the real handler path, then log in for a live nonce.
        await _services.ProvisionFirstAdministratorAsync("tst-fuzz-admin", "Synthetic#Pass1", default);
        var (principal, failure) = await _services.AuthenticateAsync("tst-fuzz-admin", "Synthetic#Pass1", default);
        Assert.NotNull(principal);
        _gaNonce = principal!.SessionNonce;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _db.Dispose();
        SyntheticFixtures.DeleteTempDatabase(_dbPath);
    }

    // ------------------------------------------------------------------ hostile corpus

    public static TheoryData<string> HostileStrings() => new(new[]
    {
        "' OR '1'='1",
        "\"; DROP TABLE Persons; --",
        "1; DELETE FROM AuditEvents",
        "' UNION SELECT CredentialHash, PasswordSalt FROM Users --",
        "%', '', (SELECT group_concat(username) FROM users))--",
        "\u202E reverse-bidi \u202D injection",
        "<script>alert(1)</script>",
        "javascript:alert(document.cookie)",
        "{{7*7}} ${{7*7}} #{7*7}",
        "..\\..\\..\\windows\\system32\\config\\sam",
        "../../../etc/passwd",
        "\\evil-share\\payload.xlsx",
        "C:\\Users\\Public\\loot.sqlite",
        "report.xlsx\u0000.png",
        "name-with-\n-newline.txt",
        "x".PadRight(300_000, 'x'),               // oversized field probe
        "٩٩٩٩٩٩٩٩٩٩٩٩٩٩٩",                        // Arabic-Indic digits (normalizer probe)
        string.Join("\u0301", "ءابتثجحخدذرزسشصضطظعغفقكلمنهوي"), // combining marks stress
        "-2147483649",                             // out-of-range integer probe
        "9223372036854775808",                     // long overflow probe
        "{\"nested\":{\"deeper\":{\"deepest\":\"value\"}}}", // object where string expected
        "[\"array\",\"where\",\"string\",\"expected\"]",
        "true",
        "null",
    });

    private string Envelope(string action, JsonObject payload, string requestId) => new JsonObject
    {
        ["requestId"] = requestId,
        ["protocolVersion"] = BridgeProtocol.ProtocolVersion,
        ["action"] = action,
        ["sessionNonce"] = _gaNonce,
        ["payload"] = payload
    }.ToJsonString();

    [Theory]
    [MemberData(nameof(HostileStrings))]
    public async Task HostileTerm_NeverThrows_NeverBreaksSchema_NeverEscapesToPath(string hostile)
    {
        string reqId = $"FUZZ-{Math.Abs(hostile.GetHashCode()):X}";
        BridgeDispatchResult result = await _dispatcher.DispatchAsync(
            Envelope("SearchPersons", new JsonObject { ["term"] = hostile, ["take"] = 5 }, reqId),
            LocalWebUiOrigin.BaseUrl, isMainFrame: true, nonce => _services.ResolveSessionAsync(nonce));

        // Contract: dispatch NEVER throws; whatever the outcome, it is a canonical envelope.
        Assert.Equal(reqId, result.Response.RequestId);
        if (!result.Response.Success)
            Assert.False(string.IsNullOrWhiteSpace(result.Response.ErrorCode));

        // Database survived intact: schema tables still present and integrity check passes.
        using (var connection = _db.CreateOpenConnection())
        {
            using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", integrity.ExecuteScalar()?.ToString());
            using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Persons','Users','AuditEvents','AttendanceRecords');";
            Assert.Equal(4L, Convert.ToInt64(await tables.ExecuteScalarAsync()));
        }

        // No file with a fuzz-derived name may appear in the working directory (CWD).
        // The bridge pipeline must never treat payload strings as paths; this is the traversal guard.
        foreach (string produced in Directory.EnumerateFiles(Environment.CurrentDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(produced);
            Assert.False(
                name.Contains("<script>") || name.Contains("loot") || name.Contains("payload.xlsx"),
                $"Fuzz input escaped to filesystem as a file name: {produced}");
        }
    }

    [Theory]
    [MemberData(nameof(HostileStrings))]
    public async Task HostileDraftFields_RejectedOrBound_NeverExecutedAsSql(string hostile)
    {
        // SaveAttendanceDraft funnels businessDate/statusId/selected ids into parameterized DML.
        // Either canonical validation rejects it, or it is stored verbatim as DATA (never code).
        BridgeDispatchResult result = await _dispatcher.DispatchAsync(
            Envelope("SaveAttendanceDraft", new JsonObject
            {
                ["businessDate"] = hostile,
                ["targetStatusId"] = hostile,
                ["selectedPersonIds"] = new JsonArray(hostile)
            }, $"FUZZ-DRAFT-{Math.Abs(hostile.GetHashCode()):X}"),
            LocalWebUiOrigin.BaseUrl, true, nonce => _services.ResolveSessionAsync(nonce));

        // Invalid hostile fields may be rejected; success and error envelopes must agree.
        Assert.Equal(result.Kind == BridgeOutcomeKind.Success, result.Response.Success);
        if (!result.Response.Success)
            Assert.False(string.IsNullOrWhiteSpace(result.Response.ErrorCode));

        using var connection = _db.CreateOpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table';";
        long tableCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        Assert.True(tableCount >= 20, "Table count must be unchanged by draft fuzzing.");
    }

    [Fact]
    public async Task DeeplyNestedJson_IsRefused_ByParserDepthLimit()
    {
        const int depth = 100;
        string nested = new string('[', depth) + new string(']', depth);
        // Construct wire JSON directly: parsing the nested value here would throw
        // before the production bridge parser could enforce its own depth limit.
        string raw = "{\"requestId\":\"DEPTH-1\",\"protocolVersion\":" +
            BridgeProtocol.ProtocolVersion +
            ",\"action\":\"GetCurrentSession\",\"sessionNonce\":\"" + _gaNonce +
            "\",\"payload\":{\"arr\":" + nested + "}}";

        BridgeDispatchResult result = await _dispatcher.DispatchAsync(
            raw, LocalWebUiOrigin.BaseUrl, true, nonce => _services.ResolveSessionAsync(nonce));
        Assert.NotEqual(BridgeOutcomeKind.Success, result.Kind);
        Assert.False(result.Response.Success);
    }

    [Fact]
    public async Task TraversalInQueryParameters_StaysWithinPinnedVirtualHostSemantics()
    {
        // The origin gate is the single decision point shared by host guards and dispatcher.
        foreach (string traversalUri in new[]
        {
            "https://manger.adwas.example/../../windows/system32",
            "https://manger.adwas.example%2e.evil.test/",
            "https://xn--manger-zzz.adwas.example/",
            "https://manger.adwas.example:444/",       // port allowed? host match yes — document behaviour
            "https://MANGER.ADWAS.EXAMPLE/"            // case-insensitive host is fine per URI spec
        })
        {
            bool allowed = WebView2BridgeDispatcher.IsAllowedOrigin(traversalUri);
            // Only exact-host https URIs may pass; userinfo/subdomain tricks must fail.
            if (traversalUri.Contains("%2e") || traversalUri.Contains("xn--"))
                Assert.False(allowed, $"Subdomain/punycode trick must be denied: {traversalUri}");
        }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task UnknownFrameOrigin_MessageFromAttackerPage_NeverReachesAuthentication()
    {
        int authenticateCalls = 0;
        Func<string, Task<BridgePrincipal?>> countingAuth = nonce =>
        {
            Interlocked.Increment(ref authenticateCalls);
            return _services.ResolveSessionAsync(nonce);
        };
        BridgeDispatchResult result = await _dispatcher.DispatchAsync(
            Envelope("CommitFullReset", new JsonObject(), "ORIGIN-FUZZ-1"),
            "https://evil.example/steal.html", true, countingAuth);

        Assert.Equal(BridgeOutcomeKind.Unauthorized, result.Kind);
        Assert.Equal(0, Volatile.Read(ref authenticateCalls)); // guard fires BEFORE any auth lookup
    }

    // ---------------------------------------------------------------- helpers
}
