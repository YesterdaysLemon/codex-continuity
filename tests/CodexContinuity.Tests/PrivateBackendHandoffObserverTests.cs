using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class PrivateBackendHandoffObserverTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-private-observer-tests-{Guid.NewGuid():N}");

    public PrivateBackendHandoffObserverTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task StableOwnedBackendReturnsObservedLifecycle()
    {
        var ownershipChecks = 0;
        var readinessChecks = 0;
        var lifecycleReads = 0;
        var checks = new PrivateBackendObservationChecks(
            (_, _) =>
            {
                ownershipChecks++;
                return true;
            },
            (_, _, _) =>
            {
                readinessChecks++;
                return Task.FromResult(true);
            },
            (_, _) =>
            {
                lifecycleReads++;
                return Task.FromResult<IReadOnlyList<ThreadLifecycleStatus>>(
                    [new("active", [], Malformed: false)]);
            });

        var plan = await PrivateBackendHandoffObserver.ObserveAsync(
            root,
            backendPort: 45124,
            backendProcessId: 42,
            CancellationToken.None,
            checks);

        Assert.True(plan.BackendReady);
        Assert.Equal(1, plan.ThreadCount);
        Assert.Equal(new HandoffBlockerCounts(1, 0, 0, 0, 0), plan.Blockers);
        Assert.Contains("runningTurns", plan.Reasons);
        Assert.Equal(2, ownershipChecks);
        Assert.Equal(2, readinessChecks);
        Assert.Equal(1, lifecycleReads);
    }

    [Fact]
    public async Task OwnershipChangeAfterReadDiscardsTheObservation()
    {
        var ownershipChecks = 0;
        var checks = new PrivateBackendObservationChecks(
            (_, _) => ++ownershipChecks == 1,
            (_, _, _) => Task.FromResult(true),
            (_, _) => Task.FromResult<IReadOnlyList<ThreadLifecycleStatus>>(
                [new("idle", [], Malformed: false)]));

        var plan = await PrivateBackendHandoffObserver.ObserveAsync(
            root,
            backendPort: 45124,
            backendProcessId: 42,
            CancellationToken.None,
            checks);

        Assert.False(plan.BackendReady);
        Assert.Equal(0, plan.ThreadCount);
        Assert.Contains("backendUnavailable", plan.Reasons);
        Assert.Equal(2, ownershipChecks);
    }

    [Fact]
    public async Task PrivateReadFailureReturnsAnUnavailablePlan()
    {
        var checks = new PrivateBackendObservationChecks(
            (_, _) => true,
            (_, _, _) => Task.FromResult(true),
            (_, _) => throw new IOException("private RPC failed"));

        var plan = await PrivateBackendHandoffObserver.ObserveAsync(
            root,
            backendPort: 45124,
            backendProcessId: 42,
            CancellationToken.None,
            checks);

        Assert.False(plan.BackendReady);
        Assert.Equal(0, plan.ThreadCount);
        Assert.Contains("backendUnavailable", plan.Reasons);
    }

    [Fact]
    public async Task CallerCancellationInterruptsThePrivateRead()
    {
        var readStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = new PrivateBackendObservationChecks(
            (_, _) => true,
            (_, _, _) => Task.FromResult(true),
            async (_, cancellationToken) =>
            {
                readStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            });
        using var cancellation = new CancellationTokenSource();
        var observation = PrivateBackendHandoffObserver.ObserveAsync(
            root,
            backendPort: 45124,
            backendProcessId: 42,
            cancellation.Token,
            checks);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            observation.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
    }
}
