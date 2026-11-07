using System.Globalization;

namespace AlTamam.DesktopHost.Bridge;

// Used only by the disposable --webview-bridge-e2e host. The normal host retains
// TimeProvider.System and its real 20:00 lock; no application setting is changed.
internal sealed class WebViewSelfCheckTimeProvider : TimeProvider
{
    private readonly TimeProvider _source;
    private readonly long _started;
    private readonly DateTimeOffset _noonUtc;

    public WebViewSelfCheckTimeProvider(TimeProvider source)
    {
        _source = source;
        _started = source.GetTimestamp();
        DateTime noon = source.GetLocalNow().Date.AddHours(12);
        _noonUtc = new DateTimeOffset(noon, source.LocalTimeZone.GetUtcOffset(noon)).ToUniversalTime();
    }

    public override TimeZoneInfo LocalTimeZone => _source.LocalTimeZone;
    public override DateTimeOffset GetUtcNow() => _noonUtc + _source.GetElapsedTime(_started);
    public override long GetTimestamp() => _source.GetTimestamp();
    public override long TimestampFrequency => _source.TimestampFrequency;

    public string GetBrowserInitializationScript()
    {
        string script = """
            (() => {
              const NativeDate = Date, started = performance.now();
              const now = () => Math.floor(__SELF_CHECK_UTC_MS__ + performance.now() - started);
              window.Date = new Proxy(NativeDate, {
                construct(target, args, derived) { return Reflect.construct(target, args.length ? args : [now()], derived); },
                apply() { return new NativeDate(now()).toString(); },
                get(target, key) { return key === "now" ? now : Reflect.get(target, key); }
              });
            })();
            """;
        return script.Replace("__SELF_CHECK_UTC_MS__",
            GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
