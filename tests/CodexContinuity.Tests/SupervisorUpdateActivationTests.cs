using System.Net;
using System.Net.Sockets;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class SupervisorUpdateActivationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 15, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-update-activation-tests-{Guid.NewGuid():N}");

    public SupervisorUpdateActivationTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task StableIdleApplyClosesGatePersistsProofAndLaunchesExactSuccessor()
    {
        var fixture = PrepareMonitorState();
        await using var relay = LoopbackRelay.Start(
            fixture.Backend.PublicPort,
            fixture.Backend.BackendPort);
        SupervisorSuccessorHandoff? launched = null;
        var monitor = Monitor(
            fixture,
            createHandoff: (backend, threadIds, desktop, now) =>
                Handoff(backend, threadIds, desktop, now),
            launch: handoff => launched = handoff);

        var result = await monitor.TryLaunchAsync(
            relay,
            fixture.Backend,
            fixture.Backend.BackendPort,
            fixture.Backend.BackendProcessId,
            CancellationToken.None);

        Assert.True(result);
        Assert.True(relay.IsGated);
        Assert.NotNull(launched);
        Assert.Equal(["thread-1", "thread-2"], launched.PersistedThreadIds);
        Assert.Single(launched.DesktopProcesses);
        var persisted = new SupervisorSuccessorHandoffStore(
            ContinuityPaths.SupervisorHandoffFile(root)).Load(Now);
        Assert.Equal(SupervisorSuccessorHandoffLoadKind.Loaded, persisted.Kind);
        Assert.Equal(launched.HandoffId, persisted.Handoff!.HandoffId);
        var status = LoadApplyStatus();
        Assert.Equal(ContinuityUpdateApplyStates.Applying, status.State);
        Assert.Equal(launched.HandoffId, status.HandoffId);
    }

    [Fact]
    public async Task ActivityAppearingBehindGateCancelsApplyAndReopensEndpoint()
    {
        var fixture = PrepareMonitorState();
        await using var relay = LoopbackRelay.Start(
            fixture.Backend.PublicPort,
            fixture.Backend.BackendPort);
        var observations = new Queue<ContinuityHandoffPlan>([
            ReadyPlan(),
            BlockedPlan(),
        ]);
        var launched = false;
        var monitor = Monitor(
            fixture,
            observe: (_, _, _, _) => Task.FromResult(observations.Dequeue()),
            createHandoff: (_, _, _, _) => throw new InvalidOperationException("unexpected"),
            launch: _ => launched = true);

        var result = await monitor.TryLaunchAsync(
            relay,
            fixture.Backend,
            fixture.Backend.BackendPort,
            fixture.Backend.BackendProcessId,
            CancellationToken.None);

        Assert.False(result);
        Assert.False(relay.IsGated);
        Assert.False(launched);
        Assert.Null(LoadApplyStatus().IdleSinceUtc);
    }

    [Fact]
    public async Task FailedLaunchDeletesManifestReopensGateAndSuppressesGeneration()
    {
        var fixture = PrepareMonitorState();
        await using var relay = LoopbackRelay.Start(
            fixture.Backend.PublicPort,
            fixture.Backend.BackendPort);
        var monitor = Monitor(
            fixture,
            createHandoff: (backend, threadIds, desktop, now) =>
                Handoff(backend, threadIds, desktop, now),
            launch: _ => throw new InvalidOperationException("fixture launch failed"));

        var result = await monitor.TryLaunchAsync(
            relay,
            fixture.Backend,
            fixture.Backend.BackendPort,
            fixture.Backend.BackendProcessId,
            CancellationToken.None);

        Assert.False(result);
        Assert.False(relay.IsGated);
        Assert.Equal(
            SupervisorSuccessorHandoffLoadKind.Missing,
            new SupervisorSuccessorHandoffStore(
                ContinuityPaths.SupervisorHandoffFile(root)).Load(Now).Kind);
        var status = LoadApplyStatus();
        Assert.Equal(ContinuityUpdateApplyStates.Failed, status.State);
        Assert.Contains("fixture launch failed", status.LastError);

        var second = await monitor.TryLaunchAsync(
            relay,
            fixture.Backend,
            fixture.Backend.BackendPort,
            fixture.Backend.BackendProcessId,
            CancellationToken.None);
        Assert.False(second);
        Assert.False(relay.IsGated);
    }

    [Fact]
    public async Task SelectedSuccessorVerifiesThreadsDesktopAndReconnect()
    {
        var fixture = PrepareCompletionFixture(SupervisorSuccessorRole.Selected);
        await using var relay = LoopbackRelay.Start(
            fixture.Backend.PublicPort,
            fixture.Backend.BackendPort);

        var result = await SupervisorSuccessorCompletion.CompleteAsync(
            root,
            fixture.Successor,
            relay,
            fixture.Backend,
            fixture.Backend.BackendPort,
            fixture.Backend.BackendProcessId,
            CancellationToken.None,
            (_, _, _) => Task.FromResult<IReadOnlyList<string>>(
                ["thread-1", "thread-2", "thread-new"]),
            () => RunningDesktop(),
            activeConnections: () => 1,
            utcNow: () => Now,
            delay: (_, _) => Task.CompletedTask,
            createRollback: (_, _, _) => throw new InvalidOperationException("unexpected"),
            launchRollback: _ => throw new InvalidOperationException("unexpected"));

        Assert.Equal(SupervisorSuccessorCompletionKind.Continue, result);
        Assert.Equal(ContinuityUpdateApplyStates.Active, LoadApplyStatus().State);
        Assert.Equal(
            SupervisorSuccessorHandoffLoadKind.Missing,
            new SupervisorSuccessorHandoffStore(
                ContinuityPaths.SupervisorHandoffFile(root)).Load(Now).Kind);
    }

    [Theory]
    [InlineData("missingThread")]
    [InlineData("desktopExited")]
    [InlineData("noReconnect")]
    public async Task FailedSelectedVerificationLaunchesExactRollback(string failure)
    {
        var fixture = PrepareCompletionFixture(SupervisorSuccessorRole.Selected);
        await using var relay = LoopbackRelay.Start(
            fixture.Backend.PublicPort,
            fixture.Backend.BackendPort);
        var times = new Queue<DateTimeOffset>([Now, Now + TimeSpan.FromSeconds(16)]);
        SupervisorSuccessorHandoff? launched = null;
        var rollback = Handoff(
            fixture.Backend,
            fixture.Successor.Handoff.PersistedThreadIds,
            fixture.Successor.Handoff.DesktopProcesses,
            Now) with
        {
            RunningBuild = fixture.Successor.Handoff.SelectedBuild,
            SelectedBuild = fixture.Successor.Handoff.SelectedBuild,
            RollbackBuild = fixture.Successor.Handoff.RollbackBuild,
        };

        var result = await SupervisorSuccessorCompletion.CompleteAsync(
            root,
            fixture.Successor,
            relay,
            fixture.Backend,
            fixture.Backend.BackendPort,
            fixture.Backend.BackendProcessId,
            CancellationToken.None,
            (_, _, _) => Task.FromResult<IReadOnlyList<string>>(failure == "missingThread"
                ? ["thread-1"]
                : ["thread-1", "thread-2"]),
            () => failure == "desktopExited"
                ? new(CodexDesktopObservationKind.NotRunning, [], "gone")
                : RunningDesktop(),
            activeConnections: () => failure == "noReconnect" ? 0 : 1,
            utcNow: () => times.Count > 0 ? times.Dequeue() : Now + TimeSpan.FromSeconds(16),
            delay: (_, _) => Task.CompletedTask,
            createRollback: (_, _, _) => rollback,
            launchRollback: handoff => launched = handoff);

        Assert.Equal(SupervisorSuccessorCompletionKind.RollbackLaunched, result);
        Assert.True(relay.IsGated);
        Assert.Same(rollback, launched);
        var status = LoadApplyStatus();
        Assert.Equal(ContinuityUpdateApplyStates.Applying, status.State);
        Assert.NotNull(status.LastError);
        Assert.Equal(rollback.HandoffId, status.HandoffId);
    }

    [Fact]
    public async Task RollbackSuccessWritesTerminalSuppressionState()
    {
        var fixture = PrepareCompletionFixture(SupervisorSuccessorRole.Rollback);
        await using var relay = LoopbackRelay.Start(
            fixture.Backend.PublicPort,
            fixture.Backend.BackendPort);
        var applying = LoadApplyStatus() with { LastError = "selected verification failed" };
        new ContinuityUpdateApplyStatusStore(
            ContinuityPaths.UpdateApplyStatusFile(root)).Save(applying);

        var result = await SupervisorSuccessorCompletion.CompleteAsync(
            root,
            fixture.Successor,
            relay,
            fixture.Backend,
            fixture.Backend.BackendPort,
            fixture.Backend.BackendProcessId,
            CancellationToken.None,
            (_, _, _) => Task.FromResult<IReadOnlyList<string>>(["thread-1", "thread-2"]),
            () => RunningDesktop(),
            activeConnections: () => 1,
            utcNow: () => Now,
            delay: (_, _) => Task.CompletedTask);

        Assert.Equal(SupervisorSuccessorCompletionKind.Continue, result);
        var status = LoadApplyStatus();
        Assert.Equal(ContinuityUpdateApplyStates.RolledBack, status.State);
        Assert.Equal("selected verification failed", status.LastError);
    }

    [Fact]
    public async Task CompatibilityRollbackHelperStartsUnawarePreviousBuildAndVerifiesProof()
    {
        var fixture = PrepareCompletionFixture(SupervisorSuccessorRole.Selected);
        var applying = LoadApplyStatus() with { LastError = "selected verification failed" };
        new ContinuityUpdateApplyStatusStore(
            ContinuityPaths.UpdateApplyStatusFile(root)).Save(applying);
        var activated = false;

        var result = await SupervisorRollbackHelper.CompleteAsync(
            root,
            fixture.Successor.Handoff,
            (_, _) =>
            {
                activated = true;
                return Task.FromResult(true);
            },
            (_, _) => Task.FromResult<IReadOnlyList<string>>(
                ["thread-1", "thread-2", "thread-new"]),
            RunningDesktop,
            () => Now,
            CancellationToken.None);

        Assert.Equal(0, result);
        Assert.True(activated);
        var status = LoadApplyStatus();
        Assert.Equal(ContinuityUpdateApplyStates.RolledBack, status.State);
        Assert.Equal("selected verification failed", status.LastError);
        Assert.Equal(
            SupervisorSuccessorHandoffLoadKind.Missing,
            new SupervisorSuccessorHandoffStore(
                ContinuityPaths.SupervisorHandoffFile(root)).Load(Now).Kind);
    }

    [Fact]
    public async Task CompatibilityRollbackHelperPreservesFailureEvidence()
    {
        var fixture = PrepareCompletionFixture(SupervisorSuccessorRole.Selected);

        var result = await SupervisorRollbackHelper.CompleteAsync(
            root,
            fixture.Successor.Handoff,
            (_, _) => Task.FromResult(false),
            (_, _) => throw new InvalidOperationException("unexpected"),
            RunningDesktop,
            () => Now,
            CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(ContinuityUpdateApplyStates.Failed, LoadApplyStatus().State);
        Assert.Equal(
            SupervisorSuccessorHandoffLoadKind.Loaded,
            new SupervisorSuccessorHandoffStore(
                ContinuityPaths.SupervisorHandoffFile(root)).Load(Now).Kind);
    }

    [Fact]
    public void ErrorEvidenceIsSingleLineAndBoundedToPersistedLimit()
    {
        var result = SupervisorActivationSupport.BoundError(
            $"before\r\n{new string('x', 4096)}");

        Assert.Equal(2048, result.Length);
        Assert.DoesNotContain('\r', result);
        Assert.DoesNotContain('\n', result);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void ActivationSupportComparesOptionalPathsAndRequiresTheExpectedDesktopAnchor()
    {
        Assert.True(SupervisorActivationSupport.SameOptionalPath(null, null));
        Assert.False(SupervisorActivationSupport.SameOptionalPath(null, root));
        Assert.True(SupervisorActivationSupport.SameOptionalPath(
            Path.Combine(root, "nested", "..", "anchor"),
            Path.Combine(root, "anchor").ToUpperInvariant()));
        Assert.False(SupervisorActivationSupport.SameOptionalPath(
            Path.Combine(root, "anchor"),
            Path.Combine(root, "other")));

        var identity = new CodexDesktopProcessIdentity(100, Now.UtcTicks);
        var running = new CodexDesktopObservation(
            CodexDesktopObservationKind.Running,
            [identity],
            "running");
        Assert.True(SupervisorActivationSupport.DesktopAnchorStillRunning(
            [identity],
            running));
        Assert.False(SupervisorActivationSupport.DesktopAnchorStillRunning(
            [identity],
            new(CodexDesktopObservationKind.NotRunning, [], "gone")));
        Assert.False(SupervisorActivationSupport.DesktopAnchorStillRunning(
            [identity],
            new(
                CodexDesktopObservationKind.Running,
                [new CodexDesktopProcessIdentity(101, Now.UtcTicks)],
                "different")));
    }

    [Fact]
    public void ExactSelectedBuildRecoversAnInterruptedActivationHandoff()
    {
        var fixture = PrepareCompletionFixture(SupervisorSuccessorRole.Selected);

        var recovery = SupervisorInterruptedHandoffRecovery.Inspect(
            root,
            fixture.Successor.Handoff.SelectedBuild,
            Now);

        Assert.Equal(InterruptedSupervisorHandoffKind.SelectedSuccessor, recovery.Kind);
        Assert.Equal(
            new SupervisorSuccessorRequest(
                fixture.Successor.Handoff.HandoffId,
                SupervisorSuccessorRole.Selected),
            recovery.Request);
        Assert.Equal(ContinuityUpdateApplyStates.Applying, LoadApplyStatus().State);
    }

    [Fact]
    public void ExactRollbackBuildRecoversAsRollbackSuccessor()
    {
        var fixture = PrepareCompletionFixture(SupervisorSuccessorRole.Selected);

        var recovery = SupervisorInterruptedHandoffRecovery.Inspect(
            root,
            fixture.Successor.Handoff.RollbackBuild,
            Now);

        Assert.Equal(InterruptedSupervisorHandoffKind.RollbackSuccessor, recovery.Kind);
        Assert.Equal(SupervisorSuccessorRole.Rollback, recovery.Request!.Role);
    }

    [Fact]
    public void InterruptedCompatibilityRollbackResumesThroughHelper()
    {
        var fixture = PrepareCompletionFixture(SupervisorSuccessorRole.Selected);
        var rollbackHandoff = fixture.Successor.Handoff with
        {
            RunningBuild = fixture.Successor.Handoff.SelectedBuild,
        };
        new SupervisorSuccessorHandoffStore(
            ContinuityPaths.SupervisorHandoffFile(root)).Write(rollbackHandoff);

        var recovery = SupervisorInterruptedHandoffRecovery.Inspect(
            root,
            rollbackHandoff.SelectedBuild,
            Now);

        Assert.Equal(
            InterruptedSupervisorHandoffKind.ResumeCompatibilityRollback,
            recovery.Kind);
        Assert.Equal(SupervisorSuccessorRole.Selected, recovery.Request!.Role);
    }

    [Fact]
    public void MissingInterruptedHandoffBecomesRetryableFailureEvidence()
    {
        var fixture = PrepareCompletionFixture(SupervisorSuccessorRole.Selected);
        new SupervisorSuccessorHandoffStore(
            ContinuityPaths.SupervisorHandoffFile(root)).Delete();

        var recovery = SupervisorInterruptedHandoffRecovery.Inspect(
            root,
            fixture.Successor.Handoff.SelectedBuild,
            Now);

        Assert.Equal(InterruptedSupervisorHandoffKind.None, recovery.Kind);
        var status = LoadApplyStatus();
        Assert.Equal(ContinuityUpdateApplyStates.Failed, status.State);
        Assert.Contains("missing", status.LastError);
        Assert.Null(status.HandoffId);
    }

    [Fact]
    public void ExpiredInterruptedHandoffIsDeletedAndBecomesFailureEvidence()
    {
        var fixture = PrepareCompletionFixture(SupervisorSuccessorRole.Selected);
        var expired = fixture.Successor.Handoff with
        {
            CreatedAtUtc = Now - TimeSpan.FromMinutes(3),
            ExpiresAtUtc = Now - TimeSpan.FromMinutes(1),
        };
        new SupervisorSuccessorHandoffStore(
            ContinuityPaths.SupervisorHandoffFile(root)).Write(expired);

        var recovery = SupervisorInterruptedHandoffRecovery.Inspect(
            root,
            expired.SelectedBuild,
            Now);

        Assert.Equal(InterruptedSupervisorHandoffKind.None, recovery.Kind);
        Assert.Equal(ContinuityUpdateApplyStates.Failed, LoadApplyStatus().State);
        Assert.Equal(
            SupervisorSuccessorHandoffLoadKind.Missing,
            new SupervisorSuccessorHandoffStore(
                ContinuityPaths.SupervisorHandoffFile(root)).Load(Now).Kind);
    }

    [Fact]
    public void ForeignRecoveryBuildFailsClosedAndPreservesHandoffEvidence()
    {
        PrepareCompletionFixture(SupervisorSuccessorRole.Selected);

        var recovery = SupervisorInterruptedHandoffRecovery.Inspect(
            root,
            Build("0.5.0", "foreign.exe", 'c'),
            Now);

        Assert.Equal(InterruptedSupervisorHandoffKind.None, recovery.Kind);
        Assert.Equal(ContinuityUpdateApplyStates.Failed, LoadApplyStatus().State);
        Assert.Equal(
            SupervisorSuccessorHandoffLoadKind.Loaded,
            new SupervisorSuccessorHandoffStore(
                ContinuityPaths.SupervisorHandoffFile(root)).Load(Now).Kind);
    }

    [Theory]
    [InlineData(ContinuityUpdateApplyStates.Active)]
    [InlineData(ContinuityUpdateApplyStates.RolledBack)]
    public void TerminalProofMakesLeftoverHandoffCleanupIdempotent(string terminalState)
    {
        var fixture = PrepareCompletionFixture(SupervisorSuccessorRole.Selected);
        var status = LoadApplyStatus() with
        {
            State = terminalState,
            HandoffId = null,
            LastError = terminalState == ContinuityUpdateApplyStates.RolledBack
                ? "selected proof failed"
                : null,
        };
        new ContinuityUpdateApplyStatusStore(
            ContinuityPaths.UpdateApplyStatusFile(root)).Save(status);

        var recovery = SupervisorInterruptedHandoffRecovery.Inspect(
            root,
            fixture.Successor.Handoff.SelectedBuild,
            Now);

        Assert.Equal(InterruptedSupervisorHandoffKind.None, recovery.Kind);
        Assert.Equal(terminalState, LoadApplyStatus().State);
        Assert.Equal(
            SupervisorSuccessorHandoffLoadKind.Missing,
            new SupervisorSuccessorHandoffStore(
                ContinuityPaths.SupervisorHandoffFile(root)).Load(Now).Kind);
    }

    [Fact]
    public void CrashedSelectedSuccessorLeaseIsRebasedFromExactStatusProof()
    {
        var fixture = PrepareCompletionFixture(SupervisorSuccessorRole.Selected);
        var executable = typeof(Program).Assembly.Location;
        var identity = AutomaticUpdateRunner.ResolveBuildIdentity(executable)!;
        var selected = new SupervisorExecutableIdentity(
            identity.Version,
            executable,
            identity.ExecutableSha256);
        var original = fixture.Successor.Handoff with { SelectedBuild = selected };
        var crashedOwner = int.MaxValue;
        var movedLease = original.Backend with { OwnerSupervisorProcessId = crashedOwner };
        new SupervisorSuccessorHandoffStore(
            ContinuityPaths.SupervisorHandoffFile(root)).Write(original);
        new BackendLeaseStore(
            ContinuityPaths.BackendLeaseFile(root)).Write(movedLease);
        new ContinuityUpdateApplyStatusStore(
            ContinuityPaths.UpdateApplyStatusFile(root)).Save(LoadApplyStatus() with
            {
                TargetVersion = selected.Version,
                TargetExecutableSha256 = selected.ExecutableSha256,
                HandoffId = original.HandoffId,
            });
        new SupervisorStatusStore(
            ContinuityPaths.SupervisorStatusFile(root)).Write(new(
                "running",
                crashedOwner,
                movedLease.BackendProcessId,
                movedLease.PublicPort,
                movedLease.CodexHome,
                ConsecutiveFailures: 0,
                LastExitCode: null,
                Now,
                NextRetryAtUtc: null,
                "crashed selected fixture",
                SupervisorStartedAtUtc: Now - TimeSpan.FromMinutes(1),
                SupervisorExecutable: executable));

        var recovery = SupervisorInterruptedHandoffRecovery.Inspect(
            root,
            selected,
            Now);

        Assert.Equal(InterruptedSupervisorHandoffKind.SelectedSuccessor, recovery.Kind);
        Assert.NotEqual(original.HandoffId, recovery.Request!.HandoffId);
        var rebased = new SupervisorSuccessorHandoffStore(
            ContinuityPaths.SupervisorHandoffFile(root)).Load(Now).Handoff!;
        Assert.Equal(crashedOwner, rebased.PreviousSupervisorProcessId);
        Assert.Equal(movedLease, rebased.Backend);
        Assert.Equal(rebased.HandoffId, LoadApplyStatus().HandoffId);
    }

    [Fact]
    public void ChangedLeaseWithoutExactSupervisorProofFailsClosed()
    {
        var fixture = PrepareCompletionFixture(SupervisorSuccessorRole.Selected);
        new BackendLeaseStore(
            ContinuityPaths.BackendLeaseFile(root)).Write(
                fixture.Successor.Handoff.Backend with
                {
                    OwnerSupervisorProcessId = int.MaxValue,
                });

        var recovery = SupervisorInterruptedHandoffRecovery.Inspect(
            root,
            fixture.Successor.Handoff.SelectedBuild,
            Now);

        Assert.Equal(InterruptedSupervisorHandoffKind.None, recovery.Kind);
        Assert.Equal(ContinuityUpdateApplyStates.Failed, LoadApplyStatus().State);
        Assert.Contains("ownership changed", LoadApplyStatus().LastError);
    }

    [Fact]
    public void ClosedDesktopTurnsInterruptedHandoffIntoRecoverableFailure()
    {
        var fixture = PrepareCompletionFixture(SupervisorSuccessorRole.Selected);

        var recovery = SupervisorInterruptedHandoffRecovery.Inspect(
            root,
            fixture.Successor.Handoff.SelectedBuild,
            Now,
            () => new(CodexDesktopObservationKind.NotRunning, [], "closed"));

        Assert.Equal(InterruptedSupervisorHandoffKind.None, recovery.Kind);
        Assert.Equal(ContinuityUpdateApplyStates.Failed, LoadApplyStatus().State);
        Assert.Contains("desktop closed", LoadApplyStatus().LastError);
        Assert.Equal(
            SupervisorSuccessorHandoffLoadKind.Missing,
            new SupervisorSuccessorHandoffStore(
                ContinuityPaths.SupervisorHandoffFile(root)).Load(Now).Kind);
    }

    [Fact]
    public void ConcurrentPlainStartDoesNotMutateAnActiveHandoff()
    {
        var fixture = PrepareCompletionFixture(SupervisorSuccessorRole.Selected);

        var recovery = SupervisorInterruptedHandoffRecovery.Inspect(
            root,
            fixture.Successor.Handoff.SelectedBuild,
            Now,
            () => new(CodexDesktopObservationKind.NotRunning, [], "inconclusive fixture"),
            (_, _) => SupervisorPredecessorState.Running);

        Assert.Equal(InterruptedSupervisorHandoffKind.None, recovery.Kind);
        Assert.Equal(ContinuityUpdateApplyStates.Applying, LoadApplyStatus().State);
        Assert.Equal(
            SupervisorSuccessorHandoffLoadKind.Loaded,
            new SupervisorSuccessorHandoffStore(
                ContinuityPaths.SupervisorHandoffFile(root)).Load(Now).Kind);
    }

    [Fact]
    public void BusyLifecycleLockDefersInterruptedHandoffRecoveryWithoutMutation()
    {
        var fixture = PrepareCompletionFixture(SupervisorSuccessorRole.Selected);
        using var lifecycleLock = ContinuityLifecycleLock.Acquire(root);

        var recovery = SupervisorInterruptedHandoffRecovery.Inspect(
            root,
            fixture.Successor.Handoff.SelectedBuild,
            Now);

        Assert.Equal(InterruptedSupervisorHandoffKind.None, recovery.Kind);
        Assert.Equal(ContinuityUpdateApplyStates.Applying, LoadApplyStatus().State);
        Assert.Equal(
            SupervisorSuccessorHandoffLoadKind.Loaded,
            new SupervisorSuccessorHandoffStore(
                ContinuityPaths.SupervisorHandoffFile(root)).Load(Now).Kind);
    }

    private SupervisorUpdateApplyMonitor Monitor(
        MonitorFixture fixture,
        Func<string, int, int, CancellationToken, Task<ContinuityHandoffPlan>>? observe = null,
        Func<
            BackendLease,
            IReadOnlyList<string>,
            IReadOnlyList<CodexDesktopProcessIdentity>,
            DateTimeOffset,
            SupervisorSuccessorHandoff>? createHandoff = null,
        Action<SupervisorSuccessorHandoff>? launch = null) => new(
            root,
            utcNow: () => Now,
            observePlan: observe ?? ((_, _, _, _) => Task.FromResult(ReadyPlan())),
            readThreadIds: (_, _, _) => Task.FromResult<IReadOnlyList<string>>(
                ["thread-1", "thread-2"]),
            captureDesktop: RunningDesktop,
            createHandoff,
            launchSuccessor: launch,
            stableIdleWindow: TimeSpan.FromSeconds(1));

    private MonitorFixture PrepareMonitorState()
    {
        var publicPort = AvailablePort();
        var backendPort = AvailablePort(publicPort);
        var codexHome = Path.Combine(root, "codex-home");
        var selectedExecutable = typeof(Program).Assembly.Location;
        var selected = AutomaticUpdateRunner.ResolveBuildIdentity(selectedExecutable)!;
        new InstallStateStore(ContinuityPaths.InstallStateFile(root)).Save(new InstallState(
            InstallStateStore.CurrentSchemaVersion,
            publicPort,
            selectedExecutable,
            PreviousInstalledExecutable: null,
            InstalledTrayExecutable: null,
            PreviousInstalledTrayExecutable: null,
            selected.ExecutableSha256,
            new OwnedString(null, LoopbackEndpoint.WebSocketUrl(publicPort)),
            new OwnedString(null, "false"),
            CommandPath: null,
            new OwnedString(null, "startup"),
            TrayStartupCommand: null,
            PreviousInstalledAppRegistration: null,
            InstalledAppRegistration: null,
            InstalledAtUtc: Now));
        new ContinuityUpdateApplyPolicyStore(
            ContinuityPaths.UpdateApplyPolicyFile(root)).Save(new(
                ContinuityUpdateApplyPolicy.CurrentSchemaVersion,
                AutomaticApplyWhenIdle: true,
                Generation: 1,
                UpdatedAtUtc: Now - TimeSpan.FromMinutes(1)));
        new ContinuityUpdateApplyStatusStore(
            ContinuityPaths.UpdateApplyStatusFile(root)).Save(new(
                ContinuityUpdateApplyStatus.CurrentSchemaVersion,
                ContinuityUpdateApplyStates.Waiting,
                PolicyGeneration: 1,
                selected.Version,
                selected.ExecutableSha256,
                UpdatedAtUtc: Now - TimeSpan.FromSeconds(2),
                IdleSinceUtc: Now - TimeSpan.FromSeconds(2),
                HandoffId: null,
                LastError: null));
        var backend = new BackendLease(
            BackendLease.CurrentSchemaVersion,
            OwnerSupervisorProcessId: 42,
            BackendProcessId: 43,
            PublicPort: publicPort,
            BackendPort: backendPort,
            BackendExecutable: selectedExecutable,
            CodexHome: codexHome,
            BackendStartedAtUtc: Now - TimeSpan.FromMinutes(10));
        new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root)).Write(backend);
        return new(backend);
    }

    private CompletionFixture PrepareCompletionFixture(SupervisorSuccessorRole role)
    {
        var publicPort = AvailablePort();
        var backendPort = AvailablePort(publicPort);
        var backend = new BackendLease(
            BackendLease.CurrentSchemaVersion,
            OwnerSupervisorProcessId: 50,
            BackendProcessId: 51,
            PublicPort: publicPort,
            BackendPort: backendPort,
            BackendExecutable: Path.Combine(root, "codex.exe"),
            CodexHome: Path.Combine(root, "codex-home"),
            BackendStartedAtUtc: Now - TimeSpan.FromMinutes(10));
        var handoff = Handoff(
            backend with { OwnerSupervisorProcessId = 42 },
            ["thread-1", "thread-2"],
            [new CodexDesktopProcessIdentity(100, Now.UtcTicks)],
            Now);
        new SupervisorSuccessorHandoffStore(
            ContinuityPaths.SupervisorHandoffFile(root)).Write(handoff);
        new BackendLeaseStore(
            ContinuityPaths.BackendLeaseFile(root)).Write(handoff.Backend);
        new ContinuityUpdateApplyStatusStore(
            ContinuityPaths.UpdateApplyStatusFile(root)).Save(new(
                ContinuityUpdateApplyStatus.CurrentSchemaVersion,
                ContinuityUpdateApplyStates.Applying,
                PolicyGeneration: 3,
                handoff.SelectedBuild.Version,
                handoff.SelectedBuild.ExecutableSha256,
                Now,
                IdleSinceUtc: null,
                handoff.HandoffId,
                LastError: null));
        return new(new(handoff, role), backend);
    }

    private static SupervisorSuccessorHandoff Handoff(
        BackendLease backend,
        IReadOnlyList<string> threadIds,
        IReadOnlyList<CodexDesktopProcessIdentity> desktop,
        DateTimeOffset now) => new(
            SupervisorSuccessorHandoff.CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"),
            backend.OwnerSupervisorProcessId,
            now - TimeSpan.FromMinutes(5),
            backend.PublicPort,
            backend.CodexHome,
            Build("0.4.0", "running.exe", 'a'),
            Build("0.5.0", "selected.exe", 'b'),
            Build("0.4.0", "running.exe", 'a'),
            backend,
            threadIds,
            desktop,
            now,
            now + TimeSpan.FromMinutes(1));

    private static SupervisorExecutableIdentity Build(
        string version,
        string fileName,
        char hash) => new(
            version,
            Path.Combine(Path.GetTempPath(), fileName),
            new string(hash, 64));

    private static CodexDesktopObservation RunningDesktop() => new(
        CodexDesktopObservationKind.Running,
        [new CodexDesktopProcessIdentity(100, Now.UtcTicks)],
        "running");

    private ContinuityUpdateApplyStatus LoadApplyStatus() =>
        new ContinuityUpdateApplyStatusStore(
            ContinuityPaths.UpdateApplyStatusFile(root)).Load().Status!;

    private static ContinuityHandoffPlan ReadyPlan() => new(
        "applyUpdate",
        true,
        true,
        "loaded",
        true,
        2,
        new(0, 0, 0, 0, 0),
        []);

    private static ContinuityHandoffPlan BlockedPlan() => new(
        "wait",
        false,
        true,
        "loaded",
        true,
        2,
        new(1, 0, 0, 0, 0),
        ["runningTurns"]);

    private static int AvailablePort(int? except = null)
    {
        while (true)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (port != except)
            {
                return port;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record MonitorFixture(BackendLease Backend);
    private sealed record CompletionFixture(
        AdmittedSupervisorSuccessor Successor,
        BackendLease Backend);
}
