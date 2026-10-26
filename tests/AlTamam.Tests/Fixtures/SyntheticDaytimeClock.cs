namespace AlTamam.Tests.Fixtures;

/// <summary>Business-local test clock: deterministic time of day without hardcoding a calendar date.</summary>
public sealed class SyntheticDaytimeClock : TimeProvider
{
    private DateTimeOffset _utc;
    public override TimeZoneInfo LocalTimeZone => TimeProvider.System.LocalTimeZone;
    public SyntheticDaytimeClock(int hour=12,int minute=0) => SetLocalTime(hour,minute);
    public void SetLocalTime(int hour,int minute=0)
    {
        var local=TimeProvider.System.GetLocalNow();
        _utc=new DateTimeOffset(local.Date.AddHours(hour).AddMinutes(minute),local.Offset).ToUniversalTime();
    }
    public override DateTimeOffset GetUtcNow() => _utc;
}
