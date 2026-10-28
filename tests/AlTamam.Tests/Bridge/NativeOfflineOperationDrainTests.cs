using AlTamam.Application.Bridge;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Synthetic concurrency tests for the native bridge cutover barrier.
/// NO real restore is activated; in-flight mutations may not outlive the drain.
/// </summary>
public sealed class NativeOfflineOperationDrainTests
{
    [Fact]
    public async Task StopWaitsForAllPreviouslyAcceptedRequests_AndRejectsNewWork()
    {
        var barrier = new NativeOfflineOperationDrain();
        var first = barrier.TryEnter();
        var second = barrier.TryEnter();
        Assert.NotNull(first);
        Assert.NotNull(second);
        Task stopping = barrier.StopAndDrainAsync();
        Assert.True(barrier.IsStopped);
        Assert.False(stopping.IsCompleted);
        Assert.Null(barrier.TryEnter());
        first!.Dispose();
        Assert.False(stopping.IsCompleted);
        second!.Dispose();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(barrier.TryEnter());
    }

    [Fact]
    public async Task StopWithoutInflightRequests_CompletesImmediately_AndIsIdempotent()
    {
        var barrier = new NativeOfflineOperationDrain();
        await barrier.StopAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await barrier.StopAndDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(barrier.IsStopped);
        Assert.Equal(0,barrier.ActiveCount);
        Assert.Null(barrier.TryEnter());
    }

    [Fact]
    public async Task LeaseDoubleDispose_CannotPrematurelyDrainAnotherRunningOperation()
    {
        var barrier = new NativeOfflineOperationDrain();
        var first = barrier.TryEnter()!;
        var second = barrier.TryEnter()!;
        Task stopping = barrier.StopAndDrainAsync();
        first.Dispose();
        first.Dispose();
        Assert.False(stopping.IsCompleted);
        Assert.Equal(1,barrier.ActiveCount);
        second.Dispose();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0,barrier.ActiveCount);
    }

    [Fact]
    public async Task AsyncMutation_StopsOnlyAfterItsTaskFinishes()
    {
        var barrier = new NativeOfflineOperationDrain();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = Task.Run(async () =>
        {
            using var lease = barrier.TryEnter();
            Assert.NotNull(lease);
            entered.SetResult();
            await complete.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task stopping = barrier.StopAndDrainAsync();
        Assert.False(stopping.IsCompleted);
        Assert.Null(barrier.TryEnter());
        complete.SetResult();
        await request.WaitAsync(TimeSpan.FromSeconds(5));
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConcurrentStopCalls_ShareTheSameCompletionAndNeverReopen()
    {
        var barrier = new NativeOfflineOperationDrain();
        using var lease = barrier.TryEnter();
        Assert.NotNull(lease);
        Task a = barrier.StopAndDrainAsync();
        Task b = barrier.StopAndDrainAsync();
        Assert.Same(a,b);
        Assert.False(a.IsCompleted);
        lease.Dispose();
        await Task.WhenAll(a,b).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(barrier.TryEnter());
    }

    [Fact]
    public async Task ParallelRequests_CompletionNeverUnderflowsAndStopIsLinearizable()
    {
        var barrier = new NativeOfflineOperationDrain();
        using var started = new ManualResetEventSlim(false);
        Task[] requests = Enumerable.Range(0,64).Select(_=>Task.Run(()=>
        {
            started.Wait();
            using var lease = barrier.TryEnter();
            if(lease is null)return;
            Thread.SpinWait(1_000);
        })).ToArray();
        started.Set();
        Task stopped = barrier.StopAndDrainAsync();
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));
        await stopped.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0,barrier.ActiveCount);
        Assert.Null(barrier.TryEnter());
    }
}
