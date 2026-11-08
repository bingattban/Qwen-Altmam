namespace AlTamam.Application.Bridge;

/// <summary>
/// One-shot, native-host lifetime barrier. WPF Closing cancels the physical
/// close while this awaited shutdown task drains WebView2 and all database
/// schedulers. The application single-instance mutex remains owned until
/// Windows actually closes the MainWindow after a successful drain.
/// </summary>
public sealed class NativeHostShutdownGate
{
    private readonly object _sync = new();
    private Task? _shutdownTask;
    private bool _completed;

    public bool IsCompleted
    {
        get { lock (_sync) return _completed; }
    }

    /// <summary>
    /// Start at most one shutdown; every caller awaits the SAME Task.
    /// A failed or cancelled drain must NOT be mistaken for success, retried
    /// concurrently, or permit another Windows instance to open the DB.
    /// </summary>
    public Task StopAndDrainAsync(Func<Task> nativeShutdown)
    {
        ArgumentNullException.ThrowIfNull(nativeShutdown);
        lock (_sync)
            return _shutdownTask ??= RunAsync(nativeShutdown);
    }

    private async Task RunAsync(Func<Task> nativeShutdown)
    {
        await nativeShutdown().ConfigureAwait(false);
        lock (_sync) _completed = true;
    }
}
