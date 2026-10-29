using AlTamam.Application.Scheduling;

namespace AlTamam.DesktopHost.Scheduling;

/// <summary>
/// Independent local Windows-host loop, unrelated to WebView2 counters and
/// independent of the 20:00 attendance scheduler. Every check rereads the
/// persisted admin settings. Startup runs a missed backup once for TODAY only.
/// </summary>
public sealed class AutomaticBackupSchedulerService : IAsyncDisposable
{
    private readonly AutomaticBackupService _service;
    private readonly CancellationTokenSource _cancel=new();
    private Task? _loop;
    public string? LastFailureMessage {get;private set;}
    public AutomaticBackupSchedulerService(AutomaticBackupService service)=>_service=service;

    public void Start()
    {
        if(_loop is not null)return;
        _loop=Task.Run(()=>LoopAsync(_cancel.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                await _service.RunIfDueAsync(ct).ConfigureAwait(false);
                LastFailureMessage=null;
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                LastFailureMessage=ex.Message;
                try { await _service.RecordFailureAsync(ex.Message,ct).ConfigureAwait(false); }
                catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
                catch(Exception) { /* Keep original in-memory failure when storage is unavailable. */ }
            }
            try{await Task.Delay(TimeSpan.FromMinutes(1),ct).ConfigureAwait(false);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
        }
    }
    public async ValueTask DisposeAsync()
    {
        _cancel.Cancel();
        if(_loop is not null)
        {
            try{await _loop.ConfigureAwait(false);}
            catch(OperationCanceledException){ }
        }
        _cancel.Dispose();
    }
}
