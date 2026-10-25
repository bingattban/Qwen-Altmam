using System.Security.Cryptography;
using System.Text;
using AlTamam.Infrastructure.Persistence;

namespace AlTamam.Tests.Fixtures;

/// <summary>
/// Deterministic SYNTHETIC test data only. These fixtures never contain real
/// personnel records, production keys or committed secrets (contract §2.10).
/// All IDs are clearly marked as synthetic ("TST-" prefix) and names use
/// obviously fake Arabic placeholders.
/// </summary>
public static class SyntheticFixtures
{
    /// <summary>Fixed 32-byte key material derived from a public constant label — test-only, not a secret.</summary>
    public static string TestKeyHex { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes("AlTamam.SYNTHETIC.TEST.KEY.NOT.A.SECRET"))).ToLowerInvariant();

    public const string PersonId = "TST-PERSON-0001";
    public const string SecondPersonId = "TST-PERSON-0002";
    public const string VisibleName = "شخص تجريبي";           // "test person"
    public const string NormalizedName = "شخص تجريبي";
    public const string ServiceNumberA = "تج-١";                  // textual «الرقم (ع)», synthetic
    public const string AssignmentId = "TST-ASSIGN-0001";
    public const string SecondAssignmentId = "TST-ASSIGN-0002";
    public const string SiteId = "SITE-UNSPECIFIED";               // seeded system site from V001
    public const string StatusPresentId = "STS-PRESENT";           // seeded status from V001
    public const string UserId = "TST-USER-0001";
    public const string IdempotencyPrefix = "TST-IDEM";

    public static string NewIdempotencyKey(string discriminator) =>
        $"{IdempotencyPrefix}-{discriminator}-{Guid.NewGuid():N}";

    /// <summary>Creates an isolated temp-file path for an encrypted test database (auto-cleaned by caller).</summary>
    public static string CreateTempDatabasePath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "altamam-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, AppDataLayout.DatabaseFileName);
    }

    /// <summary>Stable local test clock so attendance boundary tests never depend
    /// on the Windows build agent's actual time of day.</summary>
    public static AdjustableClock ClockAtLocalHour(int hour, int minute = 0)
    {
        var local = DateTime.Today.AddHours(hour).AddMinutes(minute);
        var utc = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
        return new AdjustableClock(utc, TimeZoneInfo.Local);
    }

    public sealed class AdjustableClock : TimeProvider
    {
        private DateTimeOffset _utcNow;
        private readonly TimeZoneInfo _zone;
        public AdjustableClock(DateTimeOffset utcNow, TimeZoneInfo? zone = null)
        {
            _utcNow = utcNow;
            _zone = zone ?? TimeZoneInfo.Utc;
        }
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override TimeZoneInfo LocalTimeZone => _zone;
        public void SetUtcNow(DateTimeOffset now) => _utcNow = now;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }

    public static void DeleteTempDatabase(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* best-effort cleanup of ephemeral test dirs */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup of ephemeral test dirs */ }
    }
}
