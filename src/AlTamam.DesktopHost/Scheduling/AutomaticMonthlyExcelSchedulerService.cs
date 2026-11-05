using AlTamam.Application.Scheduling;

namespace AlTamam.DesktopHost.Scheduling;

/// <summary>
/// Independent non-UI Windows task. Reading current settings every minute means
/// enabling/disabling the option needs no process restart. The first tick builds
/// the month after first activation; later ticks catch up missed days/months.
/// </summary>
public sealed class AutomaticMonthlyExcelSchedulerService : IAsyncDisposable
{
    private readonly AutomaticMonthlyExcelService _service;
    private readonly CancellationTokenSource _cancel = new();
    private Task? _loop;
    public string? LastFailureMessage { get; private set; }
    public AutomaticMonthlyExcelSchedulerService(AutomaticMonthlyExcelService service)
        => _service = service;

    public void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(() => LoopAsync(_cancel.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _service.RunIfDueAsync(ct).ConfigureAwait(false);
                LastFailureMessage = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LastFailureMessage = ex.Message;
                try { await _service.RecordFailureAsync(ex.Message, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception) { /* preserve the original failure for inspection */ }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancel.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cancel.Dispose();
    }
}
