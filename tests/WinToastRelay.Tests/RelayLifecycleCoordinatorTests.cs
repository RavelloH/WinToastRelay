using WinToastRelay.Models;
using WinToastRelay.Services;

namespace WinToastRelay.Tests;

public sealed class RelayLifecycleCoordinatorTests
{
    private static readonly TimeSpan CoordinationTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StartPublishesRunningOnlyAfterSuccessfulCompletionAndSuppressesDuplicateStart()
    {
        var coordinator = new RelayLifecycleCoordinator();
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStart = new TaskCompletionSource<DeliveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var start = coordinator.StartAsync(() =>
        {
            calls++;
            startEntered.SetResult();
            return finishStart.Task;
        });
        await startEntered.Task.WaitAsync(CoordinationTimeout);
        Assert.False(coordinator.IsRunning);

        var duplicate = coordinator.StartAsync(() =>
        {
            calls++;
            return Task.FromResult(new DeliveryResult(true, "unexpected"));
        });
        Assert.False(duplicate.IsCompleted);

        finishStart.SetResult(new DeliveryResult(true, "Started"));
        Assert.Equal("Started", (await start.WaitAsync(CoordinationTimeout)).Detail);
        Assert.Equal("Already running", (await duplicate.WaitAsync(CoordinationTimeout)).Detail);
        Assert.True(coordinator.IsRunning);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailedStartLeavesStoppedAndCanBeRetried()
    {
        var coordinator = new RelayLifecycleCoordinator();

        var failed = await coordinator.StartAsync(() => Task.FromResult(new DeliveryResult(false, "denied")))
            .WaitAsync(CoordinationTimeout);

        Assert.False(failed.Succeeded);
        Assert.False(coordinator.IsRunning);
        var retried = await coordinator.StartAsync(() => Task.FromResult(new DeliveryResult(true, "Started")))
            .WaitAsync(CoordinationTimeout);
        Assert.True(retried.Succeeded);
        Assert.True(coordinator.IsRunning);
    }

    [Fact]
    public async Task ThrownStartReleasesGateAndLeavesStopped()
    {
        var coordinator = new RelayLifecycleCoordinator();

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(
            () => throw new InvalidOperationException("failed start")).WaitAsync(CoordinationTimeout));

        Assert.False(coordinator.IsRunning);
        var retry = await coordinator.StartAsync(() => Task.FromResult(new DeliveryResult(true, "Started")))
            .WaitAsync(CoordinationTimeout);
        Assert.True(retry.Succeeded);
        Assert.True(coordinator.IsRunning);
    }

    [Fact]
    public async Task StopRunsEvenWhenStoppedAndSerializesFollowingStart()
    {
        var coordinator = new RelayLifecycleCoordinator();
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        var stop = coordinator.StopAsync(async () =>
        {
            order.Add("stop-entered");
            stopEntered.SetResult();
            await finishStop.Task;
            order.Add("stop-finished");
        });
        await stopEntered.Task.WaitAsync(CoordinationTimeout);
        Assert.False(coordinator.IsRunning);

        var start = coordinator.StartAsync(() =>
        {
            order.Add("start-entered");
            return Task.FromResult(new DeliveryResult(true, "Started"));
        });
        Assert.False(start.IsCompleted);
        Assert.Equal(["stop-entered"], order);

        finishStop.SetResult();
        await stop.WaitAsync(CoordinationTimeout);
        Assert.True((await start.WaitAsync(CoordinationTimeout)).Succeeded);
        Assert.Equal(["stop-entered", "stop-finished", "start-entered"], order);
        Assert.True(coordinator.IsRunning);
    }

    [Fact]
    public async Task StopFailureStillClearsRunningStateAndReleasesGate()
    {
        var coordinator = new RelayLifecycleCoordinator();
        await coordinator.StartAsync(() => Task.FromResult(new DeliveryResult(true, "Started")))
            .WaitAsync(CoordinationTimeout);
        Assert.True(coordinator.IsRunning);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StopAsync(
            () => throw new InvalidOperationException("failed cleanup")).WaitAsync(CoordinationTimeout));

        Assert.False(coordinator.IsRunning);
        var restart = await coordinator.StartAsync(() => Task.FromResult(new DeliveryResult(true, "Restarted")))
            .WaitAsync(CoordinationTimeout);
        Assert.True(restart.Succeeded);
        Assert.True(coordinator.IsRunning);
    }

    [Fact]
    public async Task StopWaitsForPendingStartAndObservesItsCompletedRunningState()
    {
        var coordinator = new RelayLifecycleCoordinator();
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStart = new TaskCompletionSource<DeliveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopSawRunning = false;
        var order = new List<string>();

        var start = coordinator.StartAsync(() =>
        {
            order.Add("start-entered");
            startEntered.SetResult();
            return finishStart.Task;
        });
        await startEntered.Task.WaitAsync(CoordinationTimeout);
        Assert.False(coordinator.IsRunning);

        var stop = coordinator.StopAsync(async () =>
        {
            stopSawRunning = coordinator.IsRunning;
            order.Add("stop-entered");
            stopEntered.SetResult();
            await finishStop.Task;
            order.Add("stop-finished");
        });
        Assert.False(stop.IsCompleted);
        Assert.Equal(["start-entered"], order);

        finishStart.SetResult(new DeliveryResult(true, "Started"));
        Assert.True((await start.WaitAsync(CoordinationTimeout)).Succeeded);
        await stopEntered.Task.WaitAsync(CoordinationTimeout);
        Assert.True(stopSawRunning);
        Assert.True(coordinator.IsRunning);

        finishStop.SetResult();
        await stop.WaitAsync(CoordinationTimeout);
        Assert.Equal(["start-entered", "stop-entered", "stop-finished"], order);
        Assert.False(coordinator.IsRunning);
    }
}
