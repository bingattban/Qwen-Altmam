using AlTamam.Application.Scheduling;

namespace AlTamam.DesktopHost.Scheduling;

/// <summary>
/// Durable in-process scheduler for the local-business-time configured automatic attendance run (07.02).
/// Lives entirely in the C# WPF host — it does NOT rely on an open HTML page or any JavaScript timer.
/// A single long-running background task sleeps until the next due instant, fires the database-backed
/// <see cref="AutoAttendanceService"/>, and on every startup performs the approved bounded catch-up
/// for dates missed while the machine was shut down (07.04).
/// </summary>
public sealed class EightPmSchedulerService : IAsyncDisposable
{
    private static readonly TimeSpan TickGranularity = TimeSpan.FromSeconds(30);
    private readonly AutoAttendanceService _attendance;
    private readonly TimeProvider _time;
    private readonly PreviewTokenRetentionService? _tokenRetention;
    private readonly CancellationTokenSource _cancel = new();
    private Task? _loop;

    public EightPmSchedulerService(AutoAttendanceService attendance, TimeProvider? timeProvider = null,
        PreviewTokenRetentionService? tokenRetention = null)
    {
        ArgumentNullException.ThrowIfNull(attendance);
        _attendance = attendance;
        _tokenRetention = tokenRetention;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Last completed run outcome surfaced to the EightPmScheduler screen (07.06).</summary>
    public IReadOnlyList<AutoAttendanceRunResult> LastRunResults { get; private set; } = [];
    public string? LastFailureMessage { get; private set; }
    public string? LastRetentionFailureMessage { get; private set; }

    /// <summary>Runs startup catch-up immediately, then keeps the configured daily schedule alive.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(() => LoopAsync(_cancel.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            // Startup recovery: covers shutdown/restart across one or more 08:00 instants.
            LastRunResults = await _attendance.RunPendingAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { LastFailureMessage = ex.Message; }
        await SweepPreviewTokensSafelyAsync(ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(await SleepUntilNextCheckAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                IReadOnlyList<AutoAttendanceRunResult> results = await _attendance.RunPendingAsync(ct).ConfigureAwait(false);
                if (results.Count > 0) LastRunResults = results;
                LastFailureMessage = null;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { LastFailureMessage = ex.Message; }
            await SweepPreviewTokensSafelyAsync(ct).ConfigureAwait(false);
        }
    }

    // Maintenance never suppresses the 08:00 job, alters its durable ledger or
    // changes LastFailureMessage (which controls its catch-up retry policy).
    private async Task SweepPreviewTokensSafelyAsync(CancellationToken ct)
    {
        if (_tokenRetention is null || ct.IsCancellationRequested) return;
        try
        {
            await _tokenRetention.RunIfDueAsync(ct).ConfigureAwait(false);
            LastRetentionFailureMessage = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { LastRetentionFailureMessage = ex.Message; }
    }

    /// <summary>
    /// Sleeps coarsely before the due hour and finely around it so a the configured attendance time boundary fires
    /// promptly and clock adjustments are re-evaluated at most one granularity window late.
    /// </summary>
    private async Task<TimeSpan> SleepUntilNextCheckAsync(CancellationToken ct)
    {
        DateTime local = _time.GetLocalNow().DateTime;
        TimeSpan dueTime = await _attendance.GetDueTimeAsync(ct).ConfigureAwait(false);
        DateTime due = local.Date.Add(dueTime);
        // If the scheduled run failed and requires recovery after
        // a default status, retry promptly after the setting is corrected.
        // SQLCipher's day ledger makes each retry safe and idempotent.
        if (local.TimeOfDay >= due.TimeOfDay && LastFailureMessage is not null)
            return TickGranularity;
        if (local.TimeOfDay >= due.TimeOfDay)
            due = due.AddDays(1);
        TimeSpan untilDue = due - local;
        if (untilDue > TimeSpan.FromMinutes(5)) return TimeSpan.FromMinutes(1); // poll settings at most once per minute
        return TickGranularity; // fine-grained checking near the boundary
    }

    public async ValueTask DisposeAsync()
    {
        _cancel.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
        _cancel.Dispose();
    }
}
