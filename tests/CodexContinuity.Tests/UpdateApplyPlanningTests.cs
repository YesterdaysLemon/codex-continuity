using Xunit;

namespace CodexContinuity.Tests;

public sealed class UpdateApplyPlanningTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 14, 0, 0, TimeSpan.Zero);
    private static readonly ContinuityBuildIdentity Target = new("0.5.0", new string('b', 64));

    [Fact]
    public void MissingOrDisabledPolicyRemainsStagedOnly()
    {
        var missing = ContinuityUpdateApplyPlanner.Evaluate(
            new(ContinuityUpdateApplyLoadKind.Missing, Policy: null),
            new(ContinuityUpdateApplyLoadKind.Missing, Status: null),
            ReadyPlan(),
            Target,
            Now);
        var disabled = ContinuityUpdateApplyPlanner.Evaluate(
            LoadedPolicy(enabled: false, generation: 3),
            new(ContinuityUpdateApplyLoadKind.Missing, Status: null),
            ReadyPlan(),
            Target,
            Now);

        Assert.Equal(ContinuityUpdateApplyStates.StagedOnly, missing.Status.State);
        Assert.Equal(0, missing.Status.PolicyGeneration);
        Assert.False(missing.BeginApply);
        Assert.Equal(ContinuityUpdateApplyStates.StagedOnly, disabled.Status.State);
        Assert.Equal(3, disabled.Status.PolicyGeneration);
        Assert.False(disabled.BeginApply);
    }

    [Fact]
    public void InvalidPolicyFailsClosed()
    {
        var decision = ContinuityUpdateApplyPlanner.Evaluate(
            new(ContinuityUpdateApplyLoadKind.Invalid, Policy: null),
            new(ContinuityUpdateApplyLoadKind.Missing, Status: null),
            ReadyPlan(),
            Target,
            Now);

        Assert.Equal(ContinuityUpdateApplyStates.Failed, decision.Status.State);
        Assert.Contains("invalid", decision.Status.LastError);
        Assert.False(decision.BeginApply);
        Assert.False(decision.PersistStatus);
    }

    [Fact]
    public void InvalidProgressStateCannotBeOverwrittenOrApplied()
    {
        var decision = ContinuityUpdateApplyPlanner.Evaluate(
            LoadedPolicy(enabled: true, generation: 4),
            new(ContinuityUpdateApplyLoadKind.Unreadable, Status: null),
            ReadyPlan(),
            Target,
            Now);

        Assert.Equal(ContinuityUpdateApplyStates.Failed, decision.Status.State);
        Assert.Contains("unreadable", decision.Status.LastError);
        Assert.False(decision.BeginApply);
        Assert.False(decision.PersistStatus);
    }

    [Fact]
    public void StableIdleWindowMustFullyElapseForTheSameTargetAndGeneration()
    {
        var first = ContinuityUpdateApplyPlanner.Evaluate(
            LoadedPolicy(enabled: true, generation: 4),
            new(ContinuityUpdateApplyLoadKind.Missing, Status: null),
            ReadyPlan(),
            Target,
            Now,
            TimeSpan.FromSeconds(30));
        var early = ContinuityUpdateApplyPlanner.Evaluate(
            LoadedPolicy(enabled: true, generation: 4),
            LoadedStatus(first.Status),
            ReadyPlan(),
            Target,
            Now + TimeSpan.FromSeconds(29),
            TimeSpan.FromSeconds(30));
        var ready = ContinuityUpdateApplyPlanner.Evaluate(
            LoadedPolicy(enabled: true, generation: 4),
            LoadedStatus(early.Status),
            ReadyPlan(),
            Target,
            Now + TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));

        Assert.Equal(Now, first.Status.IdleSinceUtc);
        Assert.False(first.BeginApply);
        Assert.Equal(Now, early.Status.IdleSinceUtc);
        Assert.False(early.BeginApply);
        Assert.True(ready.BeginApply);
    }

    [Fact]
    public void AnyBlockerOrTargetChangeResetsTheIdleWindow()
    {
        var waiting = WaitingStatus(generation: 4, Target, Now - TimeSpan.FromMinutes(1));
        var blocked = ContinuityUpdateApplyPlanner.Evaluate(
            LoadedPolicy(enabled: true, generation: 4),
            LoadedStatus(waiting),
            BlockedPlan(),
            Target,
            Now);
        var changedTarget = new ContinuityBuildIdentity("0.6.0", new string('c', 64));
        var changed = ContinuityUpdateApplyPlanner.Evaluate(
            LoadedPolicy(enabled: true, generation: 4),
            LoadedStatus(waiting),
            ReadyPlan(),
            changedTarget,
            Now);

        Assert.Null(blocked.Status.IdleSinceUtc);
        Assert.False(blocked.BeginApply);
        Assert.Equal(Now, changed.Status.IdleSinceUtc);
        Assert.False(changed.BeginApply);
    }

    [Fact]
    public void FailedTargetIsSuppressedUntilAnotherExplicitPolicyGeneration()
    {
        var failed = WaitingStatus(generation: 4, Target, Now) with
        {
            State = ContinuityUpdateApplyStates.RolledBack,
            IdleSinceUtc = null,
            LastError = "verification failed",
        };
        var suppressed = ContinuityUpdateApplyPlanner.Evaluate(
            LoadedPolicy(enabled: true, generation: 4),
            LoadedStatus(failed),
            ReadyPlan(),
            Target,
            Now + TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(1));
        var retried = ContinuityUpdateApplyPlanner.Evaluate(
            LoadedPolicy(enabled: true, generation: 5),
            LoadedStatus(failed),
            ReadyPlan(),
            Target,
            Now + TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(1));

        Assert.Same(failed, suppressed.Status);
        Assert.False(suppressed.BeginApply);
        Assert.False(suppressed.PersistStatus);
        Assert.Equal(5, retried.Status.PolicyGeneration);
        Assert.Equal(Now + TimeSpan.FromMinutes(1), retried.Status.IdleSinceUtc);
    }

    [Fact]
    public void SnoozeFailsClosedAndStartsANewIdleProofAfterExpiry()
    {
        var policy = LoadedPolicy(enabled: true, generation: 4) with
        {
            Policy = LoadedPolicy(enabled: true, generation: 4).Policy! with
            {
                SnoozedUntilUtc = Now + TimeSpan.FromHours(1),
            },
        };
        var priorIdle = WaitingStatus(4, Target, Now - TimeSpan.FromMinutes(1));

        var snoozed = ContinuityUpdateApplyPlanner.Evaluate(
            policy,
            LoadedStatus(priorIdle),
            ReadyPlan(),
            Target,
            Now,
            TimeSpan.FromSeconds(1));
        var resumed = ContinuityUpdateApplyPlanner.Evaluate(
            policy,
            LoadedStatus(snoozed.Status),
            ReadyPlan(),
            Target,
            Now + TimeSpan.FromHours(1),
            TimeSpan.FromSeconds(1));

        Assert.False(snoozed.BeginApply);
        Assert.Null(snoozed.Status.IdleSinceUtc);
        Assert.False(resumed.BeginApply);
        Assert.Equal(Now + TimeSpan.FromHours(1), resumed.Status.IdleSinceUtc);
    }

    [Theory]
    [InlineData(22, 59, false)]
    [InlineData(23, 0, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]
    public void OvernightActivationWindowUsesItsBoundTimeZone(
        int hour,
        int minute,
        bool eligible)
    {
        var policy = LoadedPolicy(enabled: true, generation: 4) with
        {
            Policy = LoadedPolicy(enabled: true, generation: 4).Policy! with
            {
                ActivationWindow = new(23 * 60, 7 * 60, "UTC"),
            },
        };
        var now = new DateTimeOffset(2026, 8, 24, hour, minute, 0, TimeSpan.Zero);

        var decision = ContinuityUpdateApplyPlanner.Evaluate(
            policy,
            new(ContinuityUpdateApplyLoadKind.Missing, Status: null),
            ReadyPlan(),
            Target,
            now,
            TimeSpan.FromSeconds(1));

        Assert.False(decision.BeginApply);
        Assert.Equal(eligible ? now : null, decision.Status.IdleSinceUtc);
    }

    [Fact]
    public void HandoffBindsRunningSelectedRollbackBackendThreadsAndDesktop()
    {
        var fixture = HandoffFixture();

        var handoff = SupervisorUpdateHandoffFactory.Create(
            fixture.InstallState,
            fixture.UpdateState,
            fixture.Running,
            fixture.Selected,
            fixture.Rollback,
            45123,
            fixture.CodexHome,
            fixture.Backend,
            ["thread-1", "thread-2"],
            [new CodexDesktopProcessIdentity(45, Now.UtcTicks)],
            supervisorProcessId: 42,
            supervisorStartedAtUtc: Now - TimeSpan.FromMinutes(5),
            nowUtc: Now);

        Assert.Equal(fixture.Running, handoff.RunningBuild);
        Assert.Equal(fixture.Selected, handoff.SelectedBuild);
        Assert.Equal(fixture.Rollback, handoff.RollbackBuild);
        Assert.Equal(fixture.Backend, handoff.Backend);
        Assert.Equal(["thread-1", "thread-2"], handoff.PersistedThreadIds);
        Assert.Single(handoff.DesktopProcesses);
        Assert.Equal(
            SupervisorSuccessorHandoff.MaximumLifetime,
            handoff.ExpiresAtUtc - handoff.CreatedAtUtc);
    }

    [Theory]
    [InlineData("selectedInstall")]
    [InlineData("rollbackPath")]
    [InlineData("rollbackIdentity")]
    [InlineData("selectedSameAsRunning")]
    [InlineData("ledgerRunning")]
    [InlineData("ledgerRollback")]
    [InlineData("backendOwner")]
    [InlineData("desktopMissing")]
    public void HandoffFailsClosedWhenAnyProofCoordinateChanges(string coordinate)
    {
        var fixture = HandoffFixture();
        var installState = fixture.InstallState;
        var updateState = fixture.UpdateState;
        var selected = fixture.Selected;
        var rollback = fixture.Rollback;
        var backend = fixture.Backend;
        IReadOnlyList<CodexDesktopProcessIdentity> desktop =
            [new CodexDesktopProcessIdentity(45, Now.UtcTicks)];

        switch (coordinate)
        {
            case "selectedInstall":
                installState = installState with { BinarySha256 = new string('c', 64) };
                break;
            case "rollbackPath":
                installState = installState with
                {
                    PreviousInstalledExecutable = Path.Combine(Path.GetTempPath(), "other.exe"),
                };
                break;
            case "rollbackIdentity":
                rollback = rollback with { ExecutableSha256 = new string('d', 64) };
                break;
            case "selectedSameAsRunning":
                selected = fixture.Running;
                installState = installState with
                {
                    InstalledExecutable = fixture.Running.Executable,
                    BinarySha256 = fixture.Running.ExecutableSha256,
                };
                break;
            case "ledgerRunning":
                updateState = LoadedUpdateState(
                    runningSha: new string('d', 64),
                    rollbackSha: fixture.Rollback.ExecutableSha256);
                break;
            case "ledgerRollback":
                updateState = LoadedUpdateState(
                    fixture.Running.ExecutableSha256,
                    rollbackSha: new string('d', 64));
                break;
            case "backendOwner":
                backend = backend with { OwnerSupervisorProcessId = 99 };
                break;
            case "desktopMissing":
                desktop = [];
                break;
            default:
                throw new InvalidOperationException();
        }

        Assert.Throws<InvalidDataException>(() => SupervisorUpdateHandoffFactory.Create(
            installState,
            updateState,
            fixture.Running,
            selected,
            rollback,
            45123,
            fixture.CodexHome,
            backend,
            ["thread-1"],
            desktop,
            supervisorProcessId: 42,
            supervisorStartedAtUtc: Now - TimeSpan.FromMinutes(5),
            nowUtc: Now));
    }

    private static ContinuityUpdateApplyPolicyLoadResult LoadedPolicy(
        bool enabled,
        long generation) => new(
            ContinuityUpdateApplyLoadKind.Loaded,
            new ContinuityUpdateApplyPolicy(
                ContinuityUpdateApplyPolicy.CurrentSchemaVersion,
                enabled,
                generation,
                Now));

    private static ContinuityUpdateApplyStatusLoadResult LoadedStatus(
        ContinuityUpdateApplyStatus status) => new(
            ContinuityUpdateApplyLoadKind.Loaded,
            status);

    private static ContinuityUpdateApplyStatus WaitingStatus(
        long generation,
        ContinuityBuildIdentity target,
        DateTimeOffset idleSince) => new(
            ContinuityUpdateApplyStatus.CurrentSchemaVersion,
            ContinuityUpdateApplyStates.Waiting,
            generation,
            target.Version,
            target.ExecutableSha256,
            Now,
            idleSince,
            HandoffId: null,
            LastError: null);

    private static ContinuityHandoffPlan ReadyPlan() => new(
        "applyUpdate",
        TransitionReady: true,
        BackendReady: true,
        "loaded",
        PendingUpdate: true,
        ThreadCount: 2,
        new(0, 0, 0, 0, 0),
        Reasons: []);

    private static ContinuityHandoffPlan BlockedPlan() => new(
        "wait",
        TransitionReady: false,
        BackendReady: true,
        "loaded",
        PendingUpdate: true,
        ThreadCount: 2,
        new(1, 0, 0, 0, 0),
        Reasons: ["runningTurns"]);

    private static HandoffFactoryFixture HandoffFixture()
    {
        var codexHome = Path.Combine(Path.GetTempPath(), "codex-home");
        var running = Identity("0.4.0", "running.exe", 'a');
        var selected = Identity("0.5.0", "selected.exe", 'b');
        var installState = new InstallState(
            InstallStateStore.CurrentSchemaVersion,
            Port: 45123,
            InstalledExecutable: selected.Executable,
            PreviousInstalledExecutable: running.Executable,
            InstalledTrayExecutable: null,
            PreviousInstalledTrayExecutable: null,
            BinarySha256: selected.ExecutableSha256,
            new OwnedString(null, "ws://127.0.0.1:45123"),
            new OwnedString(null, "false"),
            CommandPath: null,
            new OwnedString(null, "startup"),
            TrayStartupCommand: null,
            PreviousInstalledAppRegistration: null,
            InstalledAppRegistration: null,
            InstalledAtUtc: Now - TimeSpan.FromMinutes(2));
        var backend = new BackendLease(
            BackendLease.CurrentSchemaVersion,
            OwnerSupervisorProcessId: 42,
            BackendProcessId: 43,
            PublicPort: 45123,
            BackendPort: 45124,
            BackendExecutable: Path.Combine(Path.GetTempPath(), "codex.exe"),
            CodexHome: codexHome,
            BackendStartedAtUtc: Now - TimeSpan.FromMinutes(10));
        return new(
            installState,
            LoadedUpdateState(running.ExecutableSha256, running.ExecutableSha256),
            running,
            selected,
            running,
            backend,
            codexHome);
    }

    private static ContinuityUpdateStateLoadResult LoadedUpdateState(
        string runningSha,
        string rollbackSha) => new(
            ContinuityUpdateStateLoadKind.Loaded,
            new ContinuityUpdateState(
                SchemaVersion: 1,
                TrackingStartedAtUtc: Now - TimeSpan.FromDays(1),
                LastCheckedAtUtc: Now,
                BaselineVersion: "0.4.0",
                RunningVersion: "0.4.0",
                SelectedVersion: "0.5.0",
                RunningProcessObserved: true,
                LatestVersion: "0.5.0",
                LastError: null,
                ObservedCount: 1,
                StagedCount: 1,
                AppliedCount: 0,
                Releases:
                [
                    new TrackedContinuityRelease(
                        "0.5.0",
                        Now - TimeSpan.FromHours(1),
                        Now - TimeSpan.FromHours(1),
                        StagedAtUtc: Now - TimeSpan.FromMinutes(30),
                        AppliedAtUtc: null,
                        LastError: null,
                        StagedExecutableSha256: new string('b', 64),
                        RollbackExecutableSha256: rollbackSha),
                ],
                RunningExecutableSha256: runningSha));

    private static SupervisorExecutableIdentity Identity(
        string version,
        string fileName,
        char hash) => new(
            version,
            Path.Combine(Path.GetTempPath(), fileName),
            new string(hash, 64));

    private sealed record HandoffFactoryFixture(
        InstallState InstallState,
        ContinuityUpdateStateLoadResult UpdateState,
        SupervisorExecutableIdentity Running,
        SupervisorExecutableIdentity Selected,
        SupervisorExecutableIdentity Rollback,
        BackendLease Backend,
        string CodexHome);
}
