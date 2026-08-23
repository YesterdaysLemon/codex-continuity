using System.ComponentModel;
using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class PrivateBackendGracefulStopTests
{
    [Fact]
    public async Task RejectsInvalidPreSignalStateWithoutSignalingPrivateBackend()
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync("ignore");
        var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var target = Target(backend, publicPort);
        var readyWithoutGate = new GatedHandoffDecision(
            Plan(transitionReady: true),
            GateLease: null);
        foreach (var seconds in new[] { 0, 31 })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                PrivateBackendGracefulStop.StopAsync(
                    readyWithoutGate,
                    target,
                    TimeSpan.FromSeconds(seconds),
                    CancellationToken.None));
        }
        var missingGate = await PrivateBackendGracefulStop.StopAsync(
            readyWithoutGate,
            target,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        Assert.Equal(PrivateBackendGracefulStopKind.GateUnavailable, missingGate.Kind);

        foreach (var (transitionReady, backendReady) in new[]
                 {
                     (false, true),
                     (true, false),
                 })
        {
            var gateLease = await relay.CloseGateExclusivelyAsync();
            var blocked = await PrivateBackendGracefulStop.StopAsync(
                new GatedHandoffDecision(Plan(transitionReady, backendReady), gateLease),
                target,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
            Assert.Equal(PrivateBackendGracefulStopKind.BlockedByPlan, blocked.Kind);
            Assert.False(blocked.HasPendingStopReservation);
            Assert.True(gateLease.TryOpen());
        }
        await AssertNoSignalAsync(backend);
        Assert.False(relay.IsGated);
    }

    [Fact]
    public async Task RelayBackendMismatchDoesNotSignalEitherPrivateBackend()
    {
        await using var relayBackend = await PrivateBackendTestProcess.StartAsync("ignore");
        await using var targetBackend = await PrivateBackendTestProcess.StartAsync(
            "ignore",
            relayBackend.Port);
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
        Assert.Equal(PrivateBackendGracefulStopKind.BackendIdentityMismatch, outcome.Kind);
        Assert.False(outcome.HasPendingStopReservation);
        await AssertNoSignalAsync(relayBackend);
        await AssertNoSignalAsync(targetBackend);
        Assert.True(relay.IsGated);
    }

    [Theory]
    [InlineData(false, "BackendOwnershipLost")]
    [InlineData(true, "Unknown")]
    public async Task UnverifiedListenerDoesNotSignalPrivateBackend(
        bool throwInspectionError,
        string expectedName)
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync("ignore");
        var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var decision = await ReadyDecisionAsync(relay);
        var target = Target(backend, publicPort);
        var checks = Checks((_, _) =>
            throwInspectionError ? throw new IOException("TCP table unavailable") : false);
        var outcome = await PrivateBackendGracefulStop.StopAsync(
            decision,
            target,
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            checks);
        Assert.Equal(Enum.Parse<PrivateBackendGracefulStopKind>(expectedName), outcome.Kind);
        Assert.False(outcome.HasPendingStopReservation);
        await AssertNoSignalAsync(backend);
        Assert.True(relay.IsGated);
    }

    [Fact]
    public async Task GateInvalidatedDuringOwnershipCheckDoesNotSignalPrivateBackend()
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync("ignore");
        var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var decision = await ReadyDecisionAsync(relay);
        var target = Target(backend, publicPort);
        var checks = Checks((_, _) =>
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
        Assert.Equal(PrivateBackendGracefulStopKind.GateUnavailable, outcome.Kind);
        Assert.False(outcome.HasPendingStopReservation);
        await AssertNoSignalAsync(backend);
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
        Assert.Equal(Enum.Parse<PrivateBackendGracefulStopKind>(expectedName), outcome.Kind);
        Assert.False(outcome.HasPendingStopReservation);
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
        Assert.Equal(PrivateBackendGracefulStopKind.TimedOut, outcome.Kind);
        Assert.True(outcome.HasPendingStopReservation);
        Assert.True(File.Exists(backend.SignalMarkerPath));
        Assert.False(backend.Process.HasExited);
        Assert.True(relay.IsGated);
        Assert.False(decision.GateLease!.TryOpen());
        Assert.False(decision.GateLease.TryRetargetAndOpen(
            PrivateBackendTestProcess.AvailablePort(backend.Port, publicPort)));
    }

    [Fact]
    public async Task CallerCancellationDuringStopRetainsReservationWithoutForcingBackend()
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
        var outcome = await stop;
        Assert.Equal(PrivateBackendGracefulStopKind.CallerCanceled, outcome.Kind);
        Assert.True(outcome.HasPendingStopReservation);
        Assert.True(signalObserved);
        Assert.False(backend.Process.HasExited);
        Assert.True(relay.IsGated);
        Assert.False(decision.GateLease!.TryOpen());
        Assert.False(decision.GateLease.TryRetargetAndOpen(
            PrivateBackendTestProcess.AvailablePort(backend.Port, publicPort)));
    }

    [Fact]
    public async Task StopUncertaintyAfterSignalRetainsReservation()
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync("ignore");
        var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var decision = await ReadyDecisionAsync(relay);
        var target = Target(backend, publicPort);
        using var stopCancellation = new CancellationTokenSource();
        var checks = Checks(
            (_, _) => true,
            async (ownedTarget, _, _) =>
            {
                var stop = ownedTarget.StopGracefullyAsync(
                    TimeSpan.FromSeconds(5),
                    stopCancellation.Token);
                var signalObserved = SpinWait.SpinUntil(
                    () => File.Exists(backend.SignalMarkerPath),
                    TimeSpan.FromSeconds(5));
                await stopCancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
                Assert.True(signalObserved);
                throw new Win32Exception("Exit state unavailable after signal.");
            });
        var outcome = await PrivateBackendGracefulStop.StopAsync(
            decision,
            target,
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            checks);
        Assert.Equal(PrivateBackendGracefulStopKind.Unknown, outcome.Kind);
        Assert.True(outcome.HasPendingStopReservation);
        Assert.True(File.Exists(backend.SignalMarkerPath));
        Assert.False(backend.Process.HasExited);
        Assert.True(relay.IsGated);
        Assert.False(decision.GateLease!.TryOpen());
        Assert.False(decision.GateLease.TryRetargetAndOpen(
            PrivateBackendTestProcess.AvailablePort(backend.Port, publicPort)));
    }

    [Fact]
    public async Task CallerCancellationBeforeStopDoesNotSignalPrivateBackend()
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync("ignore");
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
        await AssertNoSignalAsync(backend);
        Assert.True(relay.IsGated);
    }

    private static PrivateBackendStopTarget Target(
        PrivateBackendTestProcess backend,
        int publicPort) => PrivateBackendStopTarget.FromOwnedLease(
            backend.CreateLease(publicPort),
            backend.Process);

    private static async Task<GatedHandoffDecision> ReadyDecisionAsync(LoopbackRelay relay) =>
        new(Plan(transitionReady: true), await relay.CloseGateExclusivelyAsync());

    private static async Task AssertNoSignalAsync(PrivateBackendTestProcess backend)
    {
        await Task.Delay(200);
        Assert.False(File.Exists(backend.SignalMarkerPath));
        Assert.False(backend.Process.HasExited);
    }

    private static PrivateBackendGracefulStopChecks Checks(
        Func<int, int, bool> ownsListener,
        Func<PrivateBackendStopTarget, TimeSpan, CancellationToken,
            Task<Program.AppServerStopDisposition>>? stopTarget = null) => new(
                ownsListener,
                stopTarget ?? (static (target, timeout, cancellationToken) =>
                    target.StopGracefullyAsync(timeout, cancellationToken)));

    private static ContinuityHandoffPlan Plan(
        bool transitionReady,
        bool backendReady = true) => new(
        transitionReady ? "handoff" : "wait",
        transitionReady,
        backendReady,
        UpdateState: "loaded",
        PendingUpdate: false,
        ThreadCount: 0,
        new HandoffBlockerCounts(0, 0, 0, 0, 0),
        Reasons: []);
}
