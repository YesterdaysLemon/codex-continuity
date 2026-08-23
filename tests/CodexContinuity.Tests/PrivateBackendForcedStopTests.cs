using System.ComponentModel;
using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class PrivateBackendForcedStopTests
{
    [Theory]
    [InlineData("CallerCanceled")]
    [InlineData("Unknown")]
    public async Task OnlyTimedOutGracefulStopIsEligible(string gracefulKindName)
    {
        await using var fixture = await StopFixture.CreateAsync();
        var forceCalled = false;
        var checks = Checks(tryForceStop: _ => forceCalled = true);
        var gracefulResult = PrivateBackendGracefulStopResult.Settled(
            Enum.Parse<PrivateBackendGracefulStopKind>(gracefulKindName));
        var outcome = await PrivateBackendForcedStop.StopAsync(
            gracefulResult,
            fixture.Target,
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            checks);

        Assert.Equal(PrivateBackendForcedStopKind.NotEligible, outcome.Kind);
        Assert.False(forceCalled);
        await AssertNoForceAsync(fixture);
    }

    [Fact]
    public async Task InvalidTimeoutOrCallerCancellationCannotStartForce()
    {
        await using var fixture = await StopFixture.CreateAsync();
        var forceCalled = false;
        var checks = Checks(tryForceStop: _ => forceCalled = true);
        foreach (var seconds in new[] { 0, 31 })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                StopAsync(fixture, checks, waitTimeout: TimeSpan.FromSeconds(seconds)));
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            StopAsync(fixture, checks, canceled.Token));

        Assert.True(fixture.GracefulResult.HasPendingStopReservation);
        Assert.False(forceCalled);
        await AssertNoForceAsync(fixture);
        AssertGateProtected(fixture);
    }

    [Theory]
    [InlineData(false, "BackendOwnershipLost")]
    [InlineData(true, "Unknown")]
    public async Task UnverifiedOwnershipCannotStartForce(
        bool throwInspectionError,
        string expectedName)
    {
        await using var fixture = await StopFixture.CreateAsync();
        var forceCalled = false;
        var checks = Checks(
            (_, _) => throwInspectionError
                ? throw new IOException("TCP table unavailable")
                : false,
            _ => forceCalled = true);

        var outcome = await StopAsync(fixture, checks);

        Assert.Equal(Enum.Parse<PrivateBackendForcedStopKind>(expectedName), outcome.Kind);
        Assert.True(fixture.GracefulResult.HasPendingStopReservation);
        Assert.False(forceCalled);
        await AssertNoForceAsync(fixture);
        AssertGateProtected(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostAuthorityBeforeCommitCannotStartForce(bool cancelCaller)
    {
        await using var fixture = await StopFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var forceCalled = false;
        var checks = Checks(
            (_, _) =>
            {
                if (cancelCaller)
                {
                    cancellation.Cancel();
                }
                else
                {
                    fixture.Relay.CloseGateAsync().GetAwaiter().GetResult();
                }
                return true;
            },
            _ => forceCalled = true);

        var stop = () => StopAsync(fixture, checks, cancellation.Token);
        if (cancelCaller)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(stop);
            Assert.True(fixture.GracefulResult.HasPendingStopReservation);
        }
        else
        {
            Assert.Equal(PrivateBackendForcedStopKind.GateUnavailable, (await stop()).Kind);
        }

        Assert.False(forceCalled);
        await AssertNoForceAsync(fixture);
        AssertGateProtected(fixture);
    }

    [Theory]
    [InlineData("force", "ForcedExit")]
    [InlineData("already-exited", "AlreadyExited")]
    [InlineData("cancel-after-commit", "ForcedExit")]
    public async Task SettledHelperExitReleasesReservation(
        string scenario,
        string expectedName)
    {
        await using var fixture = await StopFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var forceCalled = false;
        if (scenario == "already-exited")
        {
            fixture.Backend.Process.Kill();
            await fixture.Backend.Process.WaitForExitAsync();
        }
        var checks = Checks(tryForceStop: target =>
        {
            forceCalled = true;
            var started = target.TryForceStop();
            if (scenario == "cancel-after-commit")
            {
                cancellation.Cancel();
            }
            return started;
        });

        var outcome = await StopAsync(
            fixture,
            checks,
            cancellation.Token,
            TimeSpan.FromSeconds(5));

        Assert.Equal(Enum.Parse<PrivateBackendForcedStopKind>(expectedName), outcome.Kind);
        Assert.False(outcome.HasPendingStopReservation);
        Assert.True(fixture.Backend.Process.HasExited);
        Assert.Equal(scenario == "already-exited", !forceCalled);
        Assert.False(File.Exists(fixture.Backend.SignalMarkerPath));
        Assert.True(fixture.GateLease.TryOpen());
    }

    [Theory]
    [InlineData("timeout", "TimedOut")]
    [InlineData("force-error", "Unknown")]
    [InlineData("mismatched-target", "GateUnavailable")]
    public async Task UnsettledForceRetainsReservation(
        string scenario,
        string expectedName)
    {
        await using var fixture = await StopFixture.CreateAsync();
        var target = scenario == "mismatched-target"
            ? PrivateBackendStopTarget.FromOwnedLease(
                fixture.Backend.CreateLease(fixture.PublicPort),
                fixture.Backend.Process)
            : fixture.Target;
        var waitCalled = false;
        var checks = Checks(
            tryForceStop: _ => scenario == "force-error"
                ? throw new Win32Exception("Force state unavailable")
                : true,
            waitForExit: (_, _) =>
            {
                waitCalled = true;
                return Task.FromResult(false);
            });

        var outcome = await StopAsync(
            fixture,
            checks,
            waitTimeout: TimeSpan.FromMilliseconds(100),
            target: target);

        Assert.Equal(Enum.Parse<PrivateBackendForcedStopKind>(expectedName), outcome.Kind);
        Assert.Equal(scenario != "mismatched-target", outcome.HasPendingStopReservation);
        Assert.Equal(scenario == "mismatched-target", fixture.GracefulResult.HasPendingStopReservation);
        Assert.Equal(scenario == "timeout", waitCalled);
        await AssertNoForceAsync(fixture);
        AssertGateProtected(fixture);
    }

    private static PrivateBackendForcedStopChecks Checks(
        Func<int, int, bool>? ownsListener = null,
        Func<PrivateBackendStopTarget, bool>? tryForceStop = null,
        Func<PrivateBackendStopTarget, TimeSpan, Task<bool>>? waitForExit = null) => new(
            ownsListener ?? WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy,
            tryForceStop ?? (static target => target.TryForceStop()),
            waitForExit ?? (static (target, timeout) => target.WaitForExitWithinAsync(timeout)));

    private static Task<PrivateBackendForcedStopResult> StopAsync(
        StopFixture fixture,
        PrivateBackendForcedStopChecks checks,
        CancellationToken cancellationToken = default,
        TimeSpan? waitTimeout = null,
        PrivateBackendStopTarget? target = null) => PrivateBackendForcedStop.StopAsync(
            fixture.GracefulResult,
            target ?? fixture.Target,
            waitTimeout ?? TimeSpan.FromSeconds(1),
            cancellationToken,
            checks);

    private static async Task AssertNoForceAsync(StopFixture fixture)
    {
        await Task.Delay(200);
        Assert.False(fixture.Backend.Process.HasExited);
        Assert.False(File.Exists(fixture.Backend.SignalMarkerPath));
    }

    private static void AssertGateProtected(StopFixture fixture)
    {
        Assert.True(fixture.Relay.IsGated);
        Assert.False(fixture.GateLease.TryOpen());
        Assert.False(fixture.GateLease.TryRetargetAndOpen(
            PrivateBackendTestProcess.AvailablePort(
                fixture.Backend.Port,
                fixture.PublicPort)));
    }

    private sealed record StopFixture(
        PrivateBackendTestProcess Backend,
        int PublicPort,
        LoopbackRelay Relay,
        RelayGateLease GateLease,
        PrivateBackendStopTarget Target,
        PrivateBackendGracefulStopResult GracefulResult) : IAsyncDisposable
    {
        internal static async Task<StopFixture> CreateAsync()
        {
            var backend = await PrivateBackendTestProcess.StartAsync("ignore");
            LoopbackRelay? relay = null;
            try
            {
                var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
                relay = LoopbackRelay.Start(publicPort, backend.Port);
                var target = PrivateBackendStopTarget.FromOwnedLease(
                    backend.CreateLease(publicPort),
                    backend.Process);
                var gateLease = await relay.CloseGateExclusivelyAsync();
                var reservation = gateLease.TryReserveBackendStop()
                    ?? throw new InvalidOperationException("Could not reserve test backend stop.");
                var gracefulResult = PrivateBackendGracefulStopResult.Pending(
                    PrivateBackendGracefulStopKind.TimedOut,
                    reservation,
                    target);
                return new StopFixture(
                    backend,
                    publicPort,
                    relay,
                    gateLease,
                    target,
                    gracefulResult);
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

        public async ValueTask DisposeAsync()
        {
            await Relay.DisposeAsync();
            await Backend.DisposeAsync();
        }
    }
}
