namespace AlTamam.Application.Bridge;

/// <summary>
/// Native-host-only linearizable barrier for switching from live WebView2 bridge
/// work to a truly quiescent offline database mode. Once stopping begins, no
/// new request/foreground activity can enter; existing operations must leave
/// before StopAndDrainAsync finishes. It does NOT shut down schedulers or DB
/// pools, and cannot by itself authorize or activate a restore.
/// </summary>
public sealed class NativeOfflineOperationDrain
{
    private readonly object _sync = new();
    private int _active;
    private bool _stopping;
    private TaskCompletionSource? _drained;

    public int ActiveCount { get { lock(_sync) return _active; } }
    public bool IsStopped { get { lock(_sync) return _stopping; } }

    /// <summary>
    /// Accept one bridge operation before its first database interaction.
    /// Null means quiescence has started and the operation MUST NOT run.
    /// Dispose the lease only after the entire async operation is finished.
    /// </summary>
    public IDisposable? TryEnter()
    {
        lock(_sync)
        {
            if(_stopping)return null;
            checked { _active++; }
            return new Lease(this);
        }
    }

    /// <summary>
    /// Synchronously closes admission at method entry and returns a task that
    /// completes only when every previously admitted operation has finished.
    /// This never cancels a running database transaction halfway through.
    /// Calling again returns the same completion task.
    /// </summary>
    public Task StopAndDrainAsync()
    {
        lock(_sync)
        {
            _stopping=true;
            _drained ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if(_active==0)_drained.TrySetResult();
            return _drained.Task;
        }
    }

    private void Leave()
    {
        lock(_sync)
        {
            _active--;
            if(_stopping && _active==0)_drained?.TrySetResult();
        }
    }

    private sealed class Lease : IDisposable
    {
        private NativeOfflineOperationDrain? _owner;
        internal Lease(NativeOfflineOperationDrain owner)=>_owner=owner;
        public void Dispose() => Interlocked.Exchange(ref _owner,null)?.Leave();
    }
}
