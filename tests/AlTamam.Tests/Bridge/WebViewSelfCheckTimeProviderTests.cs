using AlTamam.DesktopHost.Bridge;
using Xunit;

namespace AlTamam.Tests.Bridge;

public sealed class WebViewSelfCheckTimeProviderTests
{
    private sealed class RunnerClock(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public long Ticks { get; set; }
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => zone;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(19, 0)]
    [InlineData(20, 0)]
    [InlineData(23, 0)]
    [InlineData(22, 3)]
    public void RunnerHour_DoesNotMoveSyntheticBusinessDayBeyondCutoff(int hour, int offset)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("runner", TimeSpan.FromHours(offset), "runner", "runner");
        var source = new RunnerClock(new DateTimeOffset(2026, 11, 7, hour, 0, 0, TimeSpan.Zero), zone);
        var clock = new WebViewSelfCheckTimeProvider(source);
        Assert.Equal(source.GetLocalNow().Date, clock.GetLocalNow().Date);
        Assert.Equal(12, clock.GetLocalNow().Hour);
        DateTimeOffset initial = clock.GetUtcNow();
        source.Ticks += TimeSpan.FromSeconds(6).Ticks;
        Assert.Equal(initial.AddSeconds(6), clock.GetUtcNow());
        Assert.Equal(TimeSpan.FromSeconds(6), clock.GetElapsedTime(0));
        Assert.Contains(clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(), clock.GetBrowserInitializationScript());
        Assert.DoesNotContain("__SELF_CHECK_UTC_MS__", clock.GetBrowserInitializationScript());
    }
}
