using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CodexContinuity;
using CodexContinuity.ProcessHarness;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class SafePrivateBackendStopTests
{
    [Fact]
    public async Task NativeTargetStopsOnlyTheProcessOwningTheRelayedBackend()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"codex-continuity-safe-stop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var publicPort = AvailablePort();
        var backendPort = AvailablePort();
        while (backendPort == publicPort)
        {
            backendPort = AvailablePort();
        }
        var foreignPort = AvailablePort();
        while (foreignPort == publicPort || foreignPort == backendPort)
        {
            foreignPort = AvailablePort();
        }
        await using var relay = LoopbackRelay.Start(publicPort, backendPort);
        WindowsProcessGroup? process = null;
        WindowsProcessGroup? foreignProcess = null;
        try
        {
            process = StartTestBackend(testDirectory, backendPort);
            foreignProcess = StartTestBackend(testDirectory, foreignPort);
            await WaitUntilReadyAsync(backendPort);
            await WaitUntilReadyAsync(foreignPort);
            var decision = await GatedHandoffTransition.CloseAndRecomputeAsync(
                relay,
                _ => Task.FromResult(Plan(transitionReady: true)));

            Assert.Equal(
                PrivateBackendStopKind.BackendOwnershipLost,
                await SafePrivateBackendStop.StopAsync(
                    decision,
                    PrivateBackendStopTarget.From(foreignProcess),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None));
            Assert.False(foreignProcess.HasExited);
            Assert.False(process.HasExited);
            Assert.Equal(
                PrivateBackendStopKind.GracefulExit,
                await SafePrivateBackendStop.StopAsync(
                    decision,
                    PrivateBackendStopTarget.From(process),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None));
            Assert.True(process.HasExited);
            Assert.False(foreignProcess.HasExited);
            Assert.True(relay.IsGated);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            if (foreignProcess is { HasExited: false })
            {
                foreignProcess.Kill();
                await foreignProcess.WaitForExitAsync();
            }
            foreignProcess?.Dispose();
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task UnsafeOrStalePlanNeverTouchesTheBackend()
    {
        var target = UntouchableTarget();
        var blocked = new GatedHandoffDecision(Plan(transitionReady: false), GateLease: null);

        Assert.Equal(
            PrivateBackendStopKind.BlockedByPlan,
            await SafePrivateBackendStop.StopAsync(
                blocked,
                target,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                CancellationToken.None,
                (_, _) => throw new InvalidOperationException("must not inspect ownership")));

        var (relay, safe) = await SafeDecisionAsync();
        await using (relay)
        {
            await relay.CloseGateAsync();
            Assert.Equal(
                PrivateBackendStopKind.GateOwnershipLost,
                await SafePrivateBackendStop.StopAsync(
                    safe,
                    target,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None,
                    (_, _) => throw new InvalidOperationException("must not inspect ownership")));
            Assert.True(relay.IsGated);
        }
    }

    [Fact]
    public async Task ListenerOwnershipMustBeKnownBeforeGracefulStop()
    {
        var gracefulCalls = 0;
        var target = Target(
            stopGracefully: (_, _) =>
            {
                gracefulCalls++;
                return Task.FromResult(Program.AppServerStopDisposition.CleanExit);
            });
        var (lostRelay, lostDecision) = await SafeDecisionAsync();
        await using (lostRelay)
        {
            Assert.Equal(
                PrivateBackendStopKind.BackendOwnershipLost,
                await SafePrivateBackendStop.StopAsync(
                    lostDecision,
                    target,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None,
                    (_, _) => false));
        }
        var (unknownRelay, unknownDecision) = await SafeDecisionAsync();
        await using (unknownRelay)
        {
            Assert.Equal(
                PrivateBackendStopKind.BackendOwnershipUnknown,
                await SafePrivateBackendStop.StopAsync(
                    unknownDecision,
                    target,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None,
                    (_, _) => throw new Win32Exception(5, "inspection unavailable")));
            Assert.Equal(
                PrivateBackendStopKind.BackendOwnershipUnknown,
                await SafePrivateBackendStop.StopAsync(
                    unknownDecision,
                    target,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None,
                    (_, _) => throw new InvalidDataException("invalid owner table")));
        }

        Assert.Equal(0, gracefulCalls);
    }

    [Fact]
    public async Task CleanGracefulStopNeverForces()
    {
        var gracefulCalls = 0;
        var backendPort = 0;
        var (relay, decision) = await SafeDecisionAsync(port => backendPort = port);
        var target = Target(
            stopGracefully: (_, _) =>
            {
                gracefulCalls++;
                Assert.False(decision.GateLease!.TryOpen());
                Assert.False(decision.GateLease.TryRetargetAndOpen(AvailablePort()));
                return Task.FromResult(Program.AppServerStopDisposition.CleanExit);
            },
            forceStop: () => throw new InvalidOperationException("must not force"));
        await using (relay)
        {
            Assert.Equal(
                PrivateBackendStopKind.GracefulExit,
                await SafePrivateBackendStop.StopAsync(
                    decision,
                    target,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None,
                    (port, _) => port == backendPort));
            Assert.True(decision.GateLease!.TryOpen());
        }

        Assert.Equal(1, gracefulCalls);
    }

    [Fact]
    public async Task GracefulTimeoutForcesOnlyAfterOwnershipRecheck()
    {
        var ownershipChecks = 0;
        var forceCalls = 0;
        var waitCalls = 0;
        var (relay, decision) = await SafeDecisionAsync();
        var target = Target(
            stopGracefully: (_, _) => Task.FromResult(
                Program.AppServerStopDisposition.TimedOut),
            forceStop: () =>
            {
                Assert.False(decision.GateLease!.TryOpen());
                Assert.False(decision.GateLease.TryRetargetAndOpen(AvailablePort()));
                forceCalls++;
            },
            waitForExit: cancellationToken =>
            {
                Assert.True(cancellationToken.CanBeCanceled);
                waitCalls++;
                return Task.CompletedTask;
            });
        await using (relay)
        {
            Assert.Equal(
                PrivateBackendStopKind.ForcedExit,
                await SafePrivateBackendStop.StopAsync(
                    decision,
                    target,
                    TimeSpan.FromMilliseconds(20),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None,
                    (_, _) =>
                    {
                        ownershipChecks++;
                        return true;
                    }));
            Assert.True(decision.GateLease!.TryOpen());
        }

        Assert.Equal(2, ownershipChecks);
        Assert.Equal(1, forceCalls);
        Assert.Equal(1, waitCalls);
    }

    [Fact]
    public async Task GracefulTimeoutNeverForcesAfterGateOrListenerOwnershipLoss()
    {
        var forceCalls = 0;
        var (gateRelay, gateDecision) = await SafeDecisionAsync();
        await using (gateRelay)
        {
            var target = Target(
                stopGracefully: async (_, _) =>
                {
                    await gateRelay.CloseGateAsync();
                    return Program.AppServerStopDisposition.TimedOut;
                },
                forceStop: () => forceCalls++);
            Assert.Equal(
                PrivateBackendStopKind.GateOwnershipLost,
                await SafePrivateBackendStop.StopAsync(
                    gateDecision,
                    target,
                    TimeSpan.FromMilliseconds(20),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None,
                    (_, _) => true));
        }

        var ownershipChecks = 0;
        var (ownershipRelay, ownershipDecision) = await SafeDecisionAsync();
        await using (ownershipRelay)
        {
            Assert.Equal(
                PrivateBackendStopKind.BackendOwnershipLost,
                await SafePrivateBackendStop.StopAsync(
                    ownershipDecision,
                    Target(
                        stopGracefully: (_, _) => Task.FromResult(
                            Program.AppServerStopDisposition.TimedOut),
                        forceStop: () => forceCalls++),
                    TimeSpan.FromMilliseconds(20),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None,
                    (_, _) => ++ownershipChecks == 1));
        }

        Assert.Equal(0, forceCalls);
        Assert.Equal(2, ownershipChecks);
    }

    [Fact]
    public async Task CallerCancellationAfterGracefulTimeoutNeverForces()
    {
        using var cancellation = new CancellationTokenSource();
        var forceCalls = 0;
        var target = Target(
            stopGracefully: (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(Program.AppServerStopDisposition.TimedOut);
            },
            forceStop: () => forceCalls++);
        var (relay, decision) = await SafeDecisionAsync();
        await using (relay)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                SafePrivateBackendStop.StopAsync(
                    decision,
                    target,
                    TimeSpan.FromMilliseconds(20),
                    TimeSpan.FromSeconds(1),
                    cancellation.Token,
                    (_, _) => true));
        }

        Assert.Equal(0, forceCalls);
    }

    [Fact]
    public async Task CallerCancellationBeforeGracefulStopNeverSignals()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var gracefulCalls = 0;
        var target = Target(
            stopGracefully: (_, _) =>
            {
                gracefulCalls++;
                return Task.FromResult(Program.AppServerStopDisposition.CleanExit);
            });
        var (relay, decision) = await SafeDecisionAsync();
        await using (relay)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                SafePrivateBackendStop.StopAsync(
                    decision,
                    target,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    cancellation.Token,
                    (_, _) => true));
        }

        Assert.Equal(0, gracefulCalls);
    }

    [Fact]
    public async Task ForcedStopWaitIsHardBounded()
    {
        var forceCalls = 0;
        var target = Target(
            stopGracefully: (_, _) => Task.FromResult(
                Program.AppServerStopDisposition.TimedOut),
            forceStop: () => forceCalls++,
            waitForExit: cancellationToken => Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken));
        var (relay, decision) = await SafeDecisionAsync();
        await using (relay)
        {
            var stop = SafePrivateBackendStop.StopAsync(
                decision,
                target,
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(20),
                CancellationToken.None,
                (_, _) => true);
            Assert.Equal(
                PrivateBackendStopKind.ForceTimedOut,
                await stop.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(1, forceCalls);
    }

    private static PrivateBackendStopTarget UntouchableTarget() => new(
        ProcessId: 42,
        HasExited: () => throw new InvalidOperationException("must not inspect process"),
        StopGracefully: (_, _) => throw new InvalidOperationException("must not stop"),
        ForceStop: () => throw new InvalidOperationException("must not force"),
        WaitForExit: _ => throw new InvalidOperationException("must not wait"));

    private static PrivateBackendStopTarget Target(
        Func<TimeSpan, CancellationToken, Task<Program.AppServerStopDisposition>> stopGracefully,
        Action? forceStop = null,
        Func<CancellationToken, Task>? waitForExit = null) => new(
            ProcessId: 42,
            HasExited: () => false,
            StopGracefully: stopGracefully,
            ForceStop: forceStop ?? (() => { }),
            WaitForExit: waitForExit ?? (_ => Task.CompletedTask));

    private static async Task<(LoopbackRelay Relay, GatedHandoffDecision Decision)>
        SafeDecisionAsync(Action<int>? captureBackendPort = null)
    {
        var publicPort = AvailablePort();
        var backendPort = AvailablePort();
        while (backendPort == publicPort)
        {
            backendPort = AvailablePort();
        }
        captureBackendPort?.Invoke(backendPort);
        var relay = LoopbackRelay.Start(publicPort, backendPort);
        try
        {
            var decision = await GatedHandoffTransition.CloseAndRecomputeAsync(
                relay,
                _ => Task.FromResult(Plan(transitionReady: true)));
            return (relay, decision);
        }
        catch
        {
            await relay.DisposeAsync();
            throw;
        }
    }

    private static ContinuityHandoffPlan Plan(bool transitionReady) => new(
        transitionReady ? "handoff" : "wait",
        transitionReady,
        BackendReady: transitionReady,
        "loaded",
        PendingUpdate: false,
        ThreadCount: 0,
        new HandoffBlockerCounts(0, 0, 0, 0, 0),
        Reasons: []);

    private static int AvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilReadyAsync(int port)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await Program.IsReadyAsync(port, TimeSpan.FromMilliseconds(200)))
            {
                return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("The private test backend did not become ready.");
    }

    private static WindowsProcessGroup StartTestBackend(string workingDirectory, int port)
    {
        var startInfo = new ProcessStartInfo(HarnessExecutable())
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        startInfo.ArgumentList.Add("fake-self-test-app-server");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("clean");
        return WindowsProcessGroup.Start(startInfo);
    }

    private static string HarnessExecutable() => Path.ChangeExtension(
        typeof(HarnessMarker).Assembly.Location,
        ".exe");
}
