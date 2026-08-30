using System.ComponentModel;
using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class PrivateBackendTransitionTests
{
    [Fact]
    public async Task BlockedPostGatePlanReopensRelayWithoutStoppingHelper()
    {
        await using var fixture = await TransitionFixture.CreateAsync();
        var observedBehindGate = false;
        var checks = Checks((_, _, _, _) =>
        {
            observedBehindGate = fixture.Relay.IsGated;
            return Task.FromResult(BlockedPlan());
        });
        var result = await fixture.StopAsync(checks: checks);
        Assert.Equal(PrivateBackendTransitionKind.BlockedByPlan, result.Kind);
        Assert.True(observedBehindGate);
        Assert.False(result.CanStartReplacement);
        Assert.Null(result.GracefulStop);
        Assert.Null(result.ForcedStop);
        AssertHelperRunning(fixture, expectedGate: false);
    }

    [Fact]
    public async Task GracefulExitKeepsStableRelayClosedForVerifiedReplacement()
    {
        await using var fixture = await TransitionFixture.CreateAsync();
        var result = await fixture.StopAsync();
        Assert.Equal(PrivateBackendTransitionKind.GracefulExit, result.Kind);
        Assert.Equal(PrivateBackendGracefulStopKind.CleanExit, result.GracefulStop?.Kind);
        Assert.Null(result.ForcedStop);
        Assert.True(result.CanStartReplacement);
        Assert.True(fixture.Backend.Process.HasExited);
        Assert.True(File.Exists(fixture.Backend.SignalMarkerPath));
        Assert.True(fixture.Relay.IsGated);
        Assert.False(await Program.IsReadyAsync(
            fixture.PublicPort,
            TimeSpan.FromMilliseconds(100)));
        await using var replacement = await PrivateBackendTestProcess.StartAsync(
            "ignore",
            fixture.PublicPort,
            fixture.Backend.Port);
        Assert.True(result.ReplacementGateLease?.TryRetargetAndOpen(replacement.Port));
        Assert.True(await Program.IsReadyAsync(
            fixture.PublicPort,
            TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task CancellationInterruptsInFlightObservationAndReopensRelay()
    {
        await using var fixture = await TransitionFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var observationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = Checks(async (_, _, _, cancellationToken) =>
        {
            observationStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ReadyPlan();
        });
        var transition = fixture.StopAsync(cancellation.Token, checks: checks);
        await observationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transition.WaitAsync(
            TimeSpan.FromSeconds(5)));
        AssertHelperRunning(fixture, expectedGate: false);
    }

    [Fact]
    public async Task CancellationDuringGracefulStopFailsClosedWithoutForce()
    {
        await using var fixture = await TransitionFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forceCalled = false;
        var gracefulChecks = new PrivateBackendGracefulStopChecks(
            WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy,
            async (_, timeout, cancellationToken) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(2), timeout);
                stopStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Program.AppServerStopDisposition.CleanExit;
            });
        var checks = Checks(
            gracefulStop: gracefulChecks,
            forcedStop: new PrivateBackendForcedStopChecks(
                (_, _) => true,
                _ => forceCalled = true,
                (_, _) => Task.FromResult(true)));
        var transition = fixture.StopAsync(
            cancellation.Token,
            gracefulTimeout: TimeSpan.FromSeconds(2),
            checks: checks);
        await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        var result = await transition.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PrivateBackendTransitionKind.Unsafe, result.Kind);
        Assert.Equal(PrivateBackendGracefulStopKind.CallerCanceled, result.GracefulStop?.Kind);
        Assert.True(result.GracefulStop?.HasPendingStopReservation);
        Assert.Null(result.ForcedStop);
        Assert.False(result.CanStartReplacement);
        Assert.False(forceCalled);
        AssertHelperRunning(fixture, expectedGate: true);
    }

    [Theory]
    [InlineData("exit", "ForcedExit", "ForcedExit", true)]
    [InlineData("timeout", "Unsafe", "TimedOut", false)]
    [InlineData("unknown", "Unsafe", "Unknown", false)]
    public async Task CommittedTimeoutUsesExactBoundedFallback(
        string scenario,
        string expectedTransition,
        string expectedForced,
        bool replacementAllowed)
    {
        await using var fixture = await TransitionFixture.CreateAsync("ignore");
        using var cancellation = new CancellationTokenSource();
        var gracefulTimeout = TimeSpan.FromMilliseconds(125);
        var forcedTimeout = TimeSpan.FromSeconds(2);
        TimeSpan? observedGracefulTimeout = null;
        TimeSpan? observedForcedTimeout = null;
        var checks = Checks(
            gracefulStop: new PrivateBackendGracefulStopChecks(
                WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy,
                async (target, timeout, cancellationToken) =>
                {
                    observedGracefulTimeout = timeout;
                    var disposition = await target.StopGracefullyAsync(timeout, cancellationToken);
                    cancellation.Cancel();
                    return disposition;
                }),
            forcedStop: new PrivateBackendForcedStopChecks(
                WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy,
                target => scenario == "exit" ? target.TryForceStop() : true,
                (target, timeout) =>
                {
                    observedForcedTimeout = timeout;
                    return scenario switch
                    {
                        "exit" => target.WaitForExitWithinAsync(timeout),
                        "timeout" => Task.FromResult(false),
                        _ => throw new Win32Exception("forced wait state unavailable"),
                    };
                }));
        var result = await fixture.StopAsync(
            cancellation.Token,
            gracefulTimeout,
            forcedTimeout,
            checks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(gracefulTimeout, observedGracefulTimeout);
        Assert.Equal(forcedTimeout, observedForcedTimeout);
        Assert.Equal(Enum.Parse<PrivateBackendTransitionKind>(expectedTransition), result.Kind);
        Assert.Equal(PrivateBackendGracefulStopKind.TimedOut, result.GracefulStop?.Kind);
        Assert.Equal(Enum.Parse<PrivateBackendForcedStopKind>(expectedForced), result.ForcedStop?.Kind);
        Assert.Equal(!replacementAllowed, result.ForcedStop?.HasPendingStopReservation);
        Assert.Equal(replacementAllowed, result.CanStartReplacement);
        Assert.True(fixture.Relay.IsGated);
        Assert.Equal(replacementAllowed, fixture.Backend.Process.HasExited);
        Assert.True(File.Exists(fixture.Backend.SignalMarkerPath));
    }

    [Fact]
    public async Task OwnershipUncertaintyFailsClosedAndNeverForcesHelper()
    {
        await using var fixture = await TransitionFixture.CreateAsync();
        var gracefulCalled = false;
        var forceCalled = false;
        var checks = Checks(
            gracefulStop: new PrivateBackendGracefulStopChecks(
                (_, _) => false,
                (_, _, _) =>
                {
                    gracefulCalled = true;
                    throw new InvalidOperationException("must not signal unowned helper");
                }),
            forcedStop: new PrivateBackendForcedStopChecks(
                (_, _) => true,
                _ => forceCalled = true,
                (_, _) => Task.FromResult(true)));
        var result = await fixture.StopAsync(checks: checks);
        Assert.Equal(PrivateBackendTransitionKind.Unsafe, result.Kind);
        Assert.Equal(
            PrivateBackendGracefulStopKind.BackendOwnershipLost,
            result.GracefulStop?.Kind);
        Assert.Null(result.ForcedStop);
        Assert.False(result.CanStartReplacement);
        Assert.False(gracefulCalled);
        Assert.False(forceCalled);
        AssertHelperRunning(fixture, expectedGate: true);
    }

    [Fact]
    public async Task CompatibilityTimeoutReopensRelayAndNeverForcesBackend()
    {
        await using var fixture = await TransitionFixture.CreateAsync("ignore");
        var forceCalled = false;
        var checks = Checks(
            forcedStop: new PrivateBackendForcedStopChecks(
                WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy,
                _ => forceCalled = true,
                (_, _) => Task.FromResult(true)));

        var result = await fixture.StopAsync(
            gracefulTimeout: TimeSpan.FromMilliseconds(125),
            checks: checks,
            allowForcedStop: false);

        Assert.Equal(PrivateBackendTransitionKind.GracefulTimedOut, result.Kind);
        Assert.Equal(PrivateBackendGracefulStopKind.TimedOut, result.GracefulStop?.Kind);
        Assert.Null(result.ForcedStop);
        Assert.False(result.CanStartReplacement);
        Assert.False(forceCalled);
        Assert.False(fixture.Relay.IsGated);
        Assert.False(fixture.Backend.Process.HasExited);
        Assert.True(File.Exists(fixture.Backend.SignalMarkerPath));
    }

    [Fact]
    public async Task ChangedCompatibilityPreconditionReopensRelayWithoutSignalingBackend()
    {
        await using var fixture = await TransitionFixture.CreateAsync();
        var stopCalled = false;
        var graceful = new PrivateBackendGracefulStopChecks(
            WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy,
            (_, _, _) =>
            {
                stopCalled = true;
                return Task.FromResult(Program.AppServerStopDisposition.CleanExit);
            })
        {
            CanStop = _ => false,
        };

        var result = await fixture.StopAsync(
            checks: Checks(gracefulStop: graceful),
            allowForcedStop: false);

        Assert.Equal(PrivateBackendTransitionKind.PreconditionChanged, result.Kind);
        Assert.Equal(
            PrivateBackendGracefulStopKind.PreconditionChanged,
            result.GracefulStop?.Kind);
        Assert.False(stopCalled);
        AssertHelperRunning(fixture, expectedGate: false);
    }

    [Fact]
    public async Task MismatchedStableEndpointIsRejectedBeforeGating()
    {
        await using var fixture = await TransitionFixture.CreateAsync();
        var mismatchedLease = fixture.Lease with
        {
            PublicPort = PrivateBackendTestProcess.AvailablePort(
                fixture.PublicPort,
            fixture.Backend.Port),
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PrivateBackendTransition.StopForReplacementAsync(
                fixture.Relay,
                Path.GetTempPath(),
                mismatchedLease,
                fixture.Backend.Process,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                CancellationToken.None,
                Checks()));
        AssertHelperRunning(fixture, expectedGate: false);
    }

    [Fact]
    public async Task InvalidTimeoutsAreRejectedBeforeGating()
    {
        await using var fixture = await TransitionFixture.CreateAsync();
        foreach (var invalidGraceful in new[] { true, false })
        {
            foreach (var seconds in new[] { 0, 31 })
            {
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.StopAsync(
                    gracefulTimeout: TimeSpan.FromSeconds(invalidGraceful ? seconds : 1),
                    forcedWaitTimeout: TimeSpan.FromSeconds(invalidGraceful ? 1 : seconds)));
                AssertHelperRunning(fixture, expectedGate: false);
            }
        }
    }

    private static void AssertHelperRunning(TransitionFixture fixture, bool expectedGate)
    {
        Assert.Equal(expectedGate, fixture.Relay.IsGated);
        Assert.False(fixture.Backend.Process.HasExited);
        Assert.False(File.Exists(fixture.Backend.SignalMarkerPath));
    }

    private static PrivateBackendTransitionChecks Checks(
        Func<string, int, int, CancellationToken, Task<ContinuityHandoffPlan>>? observe = null,
        PrivateBackendGracefulStopChecks? gracefulStop = null,
        PrivateBackendForcedStopChecks? forcedStop = null) => new(
            observe ?? ((_, _, _, _) => Task.FromResult(ReadyPlan())),
            gracefulStop ?? PrivateBackendGracefulStopChecks.Native,
            forcedStop ?? PrivateBackendForcedStopChecks.Native);

    private static ContinuityHandoffPlan ReadyPlan() => new(
        "handoff", true, true, "loaded", false, 0, new HandoffBlockerCounts(0, 0, 0, 0, 0), []);

    private static ContinuityHandoffPlan BlockedPlan() => ReadyPlan() with
    {
        Action = "wait",
        TransitionReady = false,
        ThreadCount = 1,
        Blockers = new HandoffBlockerCounts(1, 0, 0, 0, 0),
        Reasons = ["runningTurns"],
    };

    private sealed record TransitionFixture(
        PrivateBackendTestProcess Backend, int PublicPort, LoopbackRelay Relay,
        BackendLease Lease) : IAsyncDisposable
    {
        internal static async Task<TransitionFixture> CreateAsync(string stopBehavior = "clean")
        {
            var backend = await PrivateBackendTestProcess.StartAsync(stopBehavior);
            LoopbackRelay? relay = null;
            try
            {
                var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
                relay = LoopbackRelay.Start(publicPort, backend.Port);
                return new(
                    backend,
                    publicPort,
                    relay,
                    backend.CreateLease(publicPort));
            }
            catch
            {
                if (relay is not null)
                {
                    await relay.DisposeAsync();
                }
                await backend.DisposeAsync();
                throw;
            }
        }

        internal Task<PrivateBackendTransitionResult> StopAsync(
            CancellationToken cancellationToken = default,
            TimeSpan? gracefulTimeout = null,
            TimeSpan? forcedWaitTimeout = null,
            PrivateBackendTransitionChecks? checks = null,
            bool allowForcedStop = true) =>
            PrivateBackendTransition.StopForReplacementAsync(
                Relay,
                Path.GetTempPath(),
                Lease,
                Backend.Process,
                gracefulTimeout ?? TimeSpan.FromSeconds(1),
                forcedWaitTimeout ?? TimeSpan.FromSeconds(1),
                cancellationToken,
                checks ?? Checks(),
                allowForcedStop);

        public async ValueTask DisposeAsync()
        {
            await Relay.DisposeAsync();
            await Backend.DisposeAsync();
        }
    }
}
