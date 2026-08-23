using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class PrivateBackendGracefulStopTests
{
    [Fact]
    public async Task BlockedPlanDoesNotSignalPrivateBackend()
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync();
        var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var target = Target(backend, publicPort);
        var decision = new GatedHandoffDecision(Plan(transitionReady: false), GateLease: null);

        var outcome = await PrivateBackendGracefulStop.StopAsync(
            decision,
            target,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(PrivateBackendGracefulStopKind.BlockedByPlan, outcome);
        Assert.False(File.Exists(backend.SignalMarkerPath));
        Assert.False(backend.Process.HasExited);
        Assert.False(relay.IsGated);
    }

    [Fact]
    public async Task MissingGateLeaseDoesNotSignalPrivateBackend()
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync();
        var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
        var target = Target(backend, publicPort);
        var decision = new GatedHandoffDecision(Plan(transitionReady: true), GateLease: null);

        var outcome = await PrivateBackendGracefulStop.StopAsync(
            decision,
            target,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(PrivateBackendGracefulStopKind.GateUnavailable, outcome);
        Assert.False(File.Exists(backend.SignalMarkerPath));
        Assert.False(backend.Process.HasExited);
    }

    [Fact]
    public async Task RelayBackendMismatchDoesNotSignalEitherPrivateBackend()
    {
        await using var relayBackend = await PrivateBackendTestProcess.StartAsync();
        await using var targetBackend = await PrivateBackendTestProcess.StartAsync(
            excludedPorts: relayBackend.Port);
        var publicPort = PrivateBackendTestProcess.AvailablePort(
            relayBackend.Port,
            targetBackend.Port);
        await using var relay = LoopbackRelay.Start(publicPort, relayBackend.Port);
        var decision = await ReadyDecisionAsync(relay);
        var target = Target(targetBackend, publicPort);

        var outcome = await PrivateBackendGracefulStop.StopAsync(
            decision,
            target,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(PrivateBackendGracefulStopKind.BackendIdentityMismatch, outcome);
        Assert.False(File.Exists(relayBackend.SignalMarkerPath));
        Assert.False(File.Exists(targetBackend.SignalMarkerPath));
        Assert.False(relayBackend.Process.HasExited);
        Assert.False(targetBackend.Process.HasExited);
        Assert.True(relay.IsGated);
    }

    [Theory]
    [InlineData(false, "BackendOwnershipLost")]
    [InlineData(true, "Unknown")]
    public async Task UnverifiedListenerDoesNotSignalPrivateBackend(
        bool throwInspectionError,
        string expectedName)
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync();
        var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var decision = await ReadyDecisionAsync(relay);
        var target = Target(backend, publicPort);
        var checks = new PrivateBackendGracefulStopChecks((_, _) =>
            throwInspectionError ? throw new IOException("TCP table unavailable") : false);

        var outcome = await PrivateBackendGracefulStop.StopAsync(
            decision,
            target,
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            checks);

        Assert.Equal(Enum.Parse<PrivateBackendGracefulStopKind>(expectedName), outcome);
        Assert.False(File.Exists(backend.SignalMarkerPath));
        Assert.False(backend.Process.HasExited);
        Assert.True(relay.IsGated);
    }

    [Fact]
    public async Task GateInvalidatedDuringOwnershipCheckDoesNotSignalPrivateBackend()
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync();
        var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var decision = await ReadyDecisionAsync(relay);
        var target = Target(backend, publicPort);
        var checks = new PrivateBackendGracefulStopChecks((_, _) =>
        {
            relay.CloseGateAsync().GetAwaiter().GetResult();
            return true;
        });

        var outcome = await PrivateBackendGracefulStop.StopAsync(
            decision,
            target,
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            checks);

        Assert.Equal(PrivateBackendGracefulStopKind.GateUnavailable, outcome);
        Assert.False(File.Exists(backend.SignalMarkerPath));
        Assert.False(backend.Process.HasExited);
        Assert.True(relay.IsGated);
    }

    [Theory]
    [InlineData("clean", "CleanExit")]
    [InlineData("control-exit", "WindowsControlExit")]
    [InlineData("nonzero", "UnexpectedExit")]
    public async Task MapsPrivateBackendExitBehindClosedGate(
        string stopBehavior,
        string expectedName)
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync(stopBehavior);
        var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var decision = await ReadyDecisionAsync(relay);
        var target = Target(backend, publicPort);

        var outcome = await PrivateBackendGracefulStop.StopAsync(
            decision,
            target,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(Enum.Parse<PrivateBackendGracefulStopKind>(expectedName), outcome);
        Assert.True(File.Exists(backend.SignalMarkerPath));
        Assert.True(backend.Process.HasExited);
        Assert.True(relay.IsGated);
    }

    [Fact]
    public async Task TimedOutGracefulStopLeavesPrivateBackendRunningAndRelayClosed()
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync("ignore");
        var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var decision = await ReadyDecisionAsync(relay);
        var target = Target(backend, publicPort);

        var outcome = await PrivateBackendGracefulStop.StopAsync(
            decision,
            target,
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        Assert.Equal(PrivateBackendGracefulStopKind.TimedOut, outcome);
        Assert.True(File.Exists(backend.SignalMarkerPath));
        Assert.False(backend.Process.HasExited);
        Assert.True(relay.IsGated);
    }

    [Fact]
    public async Task AlreadyExitedTargetIsReportedWithoutSendingSignal()
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync();
        var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var target = Target(backend, publicPort);
        backend.Process.Kill();
        await backend.Process.WaitForExitAsync();
        var decision = await ReadyDecisionAsync(relay);

        var outcome = await PrivateBackendGracefulStop.StopAsync(
            decision,
            target,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(PrivateBackendGracefulStopKind.AlreadyExited, outcome);
        Assert.False(File.Exists(backend.SignalMarkerPath));
        Assert.True(relay.IsGated);
    }

    [Fact]
    public async Task CallerCancellationDuringStopReleasesReservationWithoutForcingBackend()
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync("ignore");
        var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var decision = await ReadyDecisionAsync(relay);
        var target = Target(backend, publicPort);
        using var cancellation = new CancellationTokenSource();

        var stop = PrivateBackendGracefulStop.StopAsync(
            decision,
            target,
            TimeSpan.FromSeconds(5),
            cancellation.Token);
        var signalObserved = SpinWait.SpinUntil(
            () => File.Exists(backend.SignalMarkerPath),
            TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        Assert.True(signalObserved);
        Assert.False(backend.Process.HasExited);
        Assert.True(relay.IsGated);
        Assert.True(decision.GateLease!.TryOpen());
    }

    [Fact]
    public async Task CallerCancellationBeforeStopDoesNotSignalPrivateBackend()
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync();
        var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var decision = await ReadyDecisionAsync(relay);
        var target = Target(backend, publicPort);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PrivateBackendGracefulStop.StopAsync(
                decision,
                target,
                TimeSpan.FromSeconds(1),
                cancellation.Token));

        Assert.False(File.Exists(backend.SignalMarkerPath));
        Assert.False(backend.Process.HasExited);
        Assert.True(relay.IsGated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public async Task RejectsUnboundedTimeoutBeforeSignalingPrivateBackend(int seconds)
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync();
        var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
        var target = Target(backend, publicPort);
        var decision = new GatedHandoffDecision(Plan(transitionReady: true), GateLease: null);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PrivateBackendGracefulStop.StopAsync(
                decision,
                target,
                TimeSpan.FromSeconds(seconds),
                CancellationToken.None));

        Assert.False(File.Exists(backend.SignalMarkerPath));
        Assert.False(backend.Process.HasExited);
    }

    private static PrivateBackendStopTarget Target(
        PrivateBackendTestProcess backend,
        int publicPort) => PrivateBackendStopTarget.FromOwnedLease(
            backend.CreateLease(publicPort),
            backend.Process);

    private static async Task<GatedHandoffDecision> ReadyDecisionAsync(LoopbackRelay relay) =>
        new(Plan(transitionReady: true), await relay.CloseGateExclusivelyAsync());

    private static ContinuityHandoffPlan Plan(bool transitionReady) => new(
        transitionReady ? "handoff" : "wait",
        transitionReady,
        BackendReady: true,
        UpdateState: "loaded",
        PendingUpdate: false,
        ThreadCount: 0,
        new HandoffBlockerCounts(0, 0, 0, 0, 0),
        Reasons: []);
}
