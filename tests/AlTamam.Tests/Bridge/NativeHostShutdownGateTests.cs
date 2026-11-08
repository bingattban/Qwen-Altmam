using AlTamam.Application.Bridge;
using Xunit;

namespace AlTamam.Tests.Bridge;

/// <summary>
/// Deterministic tests for the host shutdown gate that prevents WPF from
/// releasing its single-instance mutex while scheduler/bridge work is live.
/// </summary>
public sealed class NativeHostShutdownGateTests
{
    [Fact]
    public async Task CloseMustRemainBlockedUntilAllAcceptedNativeWorkDrains()
    {
        var gate = new NativeHostShutdownGate();
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permitStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int invocations = 0;

        Task stopping = gate.StopAndDrainAsync(async () =>
        {
            Interlocked.Increment(ref invocations);
            stopStarted.SetResult();
            await permitStop.Task;
        });
        await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gate.IsCompleted);
        Assert.False(stopping.IsCompleted);
        Assert.Same(stopping, gate.StopAndDrainAsync(
            () => throw new InvalidOperationException("must never invoke twice")));

        permitStop.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gate.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref invocations));
    }

    [Fact]
    public async Task ShutdownFailureCannotBeMisreportedAsCompletedOrRetried()
    {
        var gate = new NativeHostShutdownGate();
        int invoked = 0;
        Task failing = gate.StopAndDrainAsync(() =>
        {
            Interlocked.Increment(ref invoked);
            throw new IOException("synthetic scheduler did not quiesce");
        });
        await Assert.ThrowsAsync<IOException>(() => failing);
        Assert.False(gate.IsCompleted);
        Assert.Same(failing, gate.StopAndDrainAsync(
            () => throw new InvalidOperationException("unsafe second cutover")));
        Assert.Equal(1, invoked);
    }

    [Fact]
    public async Task ConcurrentShutdownRequestsAllShareOneAwaitedCompletion()
    {
        var gate = new NativeHostShutdownGate();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int invocations = 0;
        var work = Enumerable.Range(0, 32).Select(_ =>
            Task.Run(() => gate.StopAndDrainAsync(async () =>
            {
                Interlocked.Increment(ref invocations);
                await release.Task;
            }))).ToArray();
        Task[] results = await Task.WhenAll(work);
        Assert.All(results, task => Assert.Same(results[0], task));
        Assert.False(gate.IsCompleted);
        release.SetResult();
        await results[0].WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gate.IsCompleted);
        Assert.Equal(1, invocations);
    }
}
