using AlTamam.Application.Scheduling;

namespace AlTamam.DesktopHost.Scheduling;

/// <summary>
/// Durable in-process scheduler for the local-business-time 20:00 automatic attendance run (07.02).
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
    private readonly CancellationTokenSource _cancel = new();
    private Task? _loop;

    public EightPmSchedulerService(AutoAttendanceService attendance, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(attendance);
        _attendance = attendance;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Last completed run outcome surfaced to the EightPmScheduler screen (07.06).</summary>
    public IReadOnlyList<AutoAttendanceRunResult> LastRunResults { get; private set; } = [];
    public string? LastFailureMessage { get; private set; }

    /// <summary>Runs startup catch-up immediately, then keeps the 20:00 schedule alive.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(() => LoopAsync(_cancel.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            // Startup recovery: covers shutdown/restart across one or more 20:00 instants.
            LastRunResults = await _attendance.RunPendingAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { LastFailureMessage = ex.Message; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SleepUntilNextCheck(), ct).ConfigureAwait(false);
                IReadOnlyList<AutoAttendanceRunResult> results = await _attendance.RunPendingAsync(ct).ConfigureAwait(false);
                if (results.Count > 0) LastRunResults = results;
                LastFailureMessage = null;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { LastFailureMessage = ex.Message; }
        }
    }

    /// <summary>
    /// Sleeps coarsely before the due hour and finely around it so a 19:59→20:00 boundary fires
    /// promptly and clock adjustments are re-evaluated at most one granularity window late.
    /// </summary>
    private TimeSpan SleepUntilNextCheck()
    {
        DateTime local = _time.GetLocalNow().DateTime;
        DateTime due = local.Date.AddHours(AutoAttendanceService.DefaultDueHour);
        // If the 20:00 run failed because the administrator has not yet chosen
        // a default status, retry promptly after the setting is corrected.
        // SQLCipher's day ledger makes each retry safe and idempotent.
        if (local.TimeOfDay >= due.TimeOfDay && LastFailureMessage is not null)
            return TickGranularity;
        if (local.TimeOfDay >= due.TimeOfDay)
            due = due.AddDays(1);
        TimeSpan untilDue = due - local;
        if (untilDue > TimeSpan.FromMinutes(5)) return untilDue - TimeSpan.FromMinutes(2); // wake slightly early
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
