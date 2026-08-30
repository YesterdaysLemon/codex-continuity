using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class BackendCompatibilityTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-backend-compatibility-{Guid.NewGuid():N}");

    [Fact]
    public async Task BridgeUpgradeWaitsForNaturalDesktopClosureThenStableInterval()
    {
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var desktop = RunningDesktop();
        var monitor = new BackendCompatibilityMonitor(
            root,
            () => @"C:\codex\current\codex.exe",
            () => desktop,
            () => now,
            inspectionInterval: TimeSpan.FromMilliseconds(1),
            stableDesktopClosedInterval: TimeSpan.FromSeconds(2));
        var lease = Lease(@"C:\codex\current\codex.exe", bridgeVersion: null);

        Assert.False((await monitor.InspectAsync(lease, CancellationToken.None)).RequiresRollover);
        Assert.Equal(
            BackendCompatibilityStateNames.WaitingForDesktopClose,
            ReadStatus().State);

        desktop = ClosedDesktop();
        now += TimeSpan.FromSeconds(1);
        Assert.False((await monitor.InspectAsync(lease, CancellationToken.None)).RequiresRollover);
        Assert.Equal(
            BackendCompatibilityStateNames.WaitingForStableClose,
            ReadStatus().State);

        now += TimeSpan.FromSeconds(3);
        Assert.True((await monitor.InspectAsync(lease, CancellationToken.None)).RequiresRollover);
        Assert.Equal(BackendCompatibilityStateNames.ReadyToRoll, ReadStatus().State);
    }

    [Fact]
    public async Task CurrentContractNeverRequestsRollover()
    {
        var monitor = new BackendCompatibilityMonitor(
            root,
            () => @"C:\codex\current\codex.exe",
            ClosedDesktop,
            inspectionInterval: TimeSpan.FromMilliseconds(1));
        var lease = Lease(
            @"C:\codex\current\codex.exe",
            DesktopMcpContractResolver.BridgeVersion);

        var decision = await monitor.InspectAsync(lease, CancellationToken.None);

        Assert.False(decision.RequiresRollover);
        Assert.Equal(BackendCompatibilityStateNames.Current, ReadStatus().State);
    }

    [Fact]
    public async Task UnsafeDesktopIdentityBlocksChangedExecutable()
    {
        var monitor = new BackendCompatibilityMonitor(
            root,
            () => @"C:\codex\new\codex.exe",
            () => new(
                CodexDesktopObservationKind.Unsafe,
                [],
                "fixture uncertainty"),
            inspectionInterval: TimeSpan.FromMilliseconds(1));
        var lease = Lease(
            @"C:\codex\old\codex.exe",
            DesktopMcpContractResolver.BridgeVersion);

        var decision = await monitor.InspectAsync(lease, CancellationToken.None);

        Assert.False(decision.RequiresRollover);
        Assert.Equal(BackendCompatibilityStateNames.Blocked, ReadStatus().State);
        Assert.True(ReadStatus().ExecutableChanged);
    }

    [Fact]
    public async Task GateRecomputationBlocksIfDesktopReopensAfterStableCloseProof()
    {
        var ready = new ContinuityHandoffPlan(
            "handoff",
            TransitionReady: true,
            BackendReady: true,
            UpdateState: "loaded",
            PendingUpdate: false,
            ThreadCount: 0,
            new HandoffBlockerCounts(0, 0, 0, 0, 0),
            Reasons: []);
        var checks = OwnedSupervisorRuntime.CompatibilityTransitionChecks(
            new PrivateBackendTransitionChecks(
                (_, _, _, _) => Task.FromResult(ready),
                PrivateBackendGracefulStopChecks.Native,
                PrivateBackendForcedStopChecks.Native),
            RunningDesktop);

        var recomputed = await checks.Observe(
            root,
            45124,
            222,
            CancellationToken.None);

        Assert.False(recomputed.TransitionReady);
        Assert.Equal("wait", recomputed.Action);
        Assert.Contains("desktopRunning", recomputed.Reasons);
        Assert.False(checks.GracefulStop.CanStop(null!));
    }

    [Fact]
    public async Task DesktopMcpReloadsOncePerVerifiedSessionFingerprint()
    {
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var fingerprint = new string('a', 64);
        var reloads = 0;
        var monitor = new DesktopMcpReloadMonitor(
            root,
            _ => Task.FromResult(AvailableContract(fingerprint)),
            (_, _, _) =>
            {
                reloads++;
                return Task.CompletedTask;
            },
            () => now,
            TimeSpan.FromMilliseconds(1));
        var lease = Lease(
            @"C:\codex\current\codex.exe",
            DesktopMcpContractResolver.BridgeVersion);

        await monitor.TryRefreshAsync(lease, 45124, 222, CancellationToken.None);
        now += TimeSpan.FromSeconds(1);
        await monitor.TryRefreshAsync(lease, 45124, 222, CancellationToken.None);
        fingerprint = new string('b', 64);
        now += TimeSpan.FromSeconds(1);
        await monitor.TryRefreshAsync(lease, 45124, 222, CancellationToken.None);

        Assert.Equal(2, reloads);
        var status = new DesktopMcpBridgeStatusStore(
            ContinuityPaths.DesktopMcpBridgeStatusFile(root)).Load();
        Assert.NotNull(status);
        Assert.Equal(DesktopMcpBridgeStateNames.ReloadQueued, status.State);
        Assert.Equal(fingerprint, status.ContractFingerprint);
    }

    private BackendCompatibilityStatus ReadStatus() =>
        new BackendCompatibilityStatusStore(
            ContinuityPaths.BackendCompatibilityStatusFile(root)).Load()
        ?? throw new Xunit.Sdk.XunitException("Compatibility status was not written.");

    private static BackendLease Lease(string executable, int? bridgeVersion) => new(
        BackendLease.CurrentSchemaVersion,
        OwnerSupervisorProcessId: 111,
        BackendProcessId: 222,
        PublicPort: 45123,
        BackendPort: 45124,
        BackendExecutable: executable,
        CodexHome: @"C:\codex-home",
        BackendStartedAtUtc: DateTimeOffset.UtcNow,
        DesktopMcpBridgeVersion: bridgeVersion);

    private static CodexDesktopObservation RunningDesktop() => new(
        CodexDesktopObservationKind.Running,
        [new CodexDesktopProcessIdentity(333, 638900000000000000)],
        "fixture");

    private static CodexDesktopObservation ClosedDesktop() => new(
        CodexDesktopObservationKind.NotRunning,
        [],
        "fixture");

    private static DesktopMcpContractResult AvailableContract(string fingerprint) => new(
        DesktopMcpContractKind.Available,
        new DesktopMcpContract(
            333,
            638900000000000000,
            $"codex-browser-use-{Guid.NewGuid():D}",
            @"C:\resources",
            @"C:\node.exe",
            @"C:\codex.exe",
            @"C:\plugins",
            new DesktopMcpLaunchManifest("cmd.exe", ["fixture"], @"C:\plugins"),
            fingerprint),
        "fixture");

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
