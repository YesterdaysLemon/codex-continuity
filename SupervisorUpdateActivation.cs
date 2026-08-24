using System.Diagnostics;

namespace CodexContinuity;

internal sealed record AdmittedSupervisorSuccessor(
    SupervisorSuccessorHandoff Handoff,
    SupervisorSuccessorRole Role);

internal enum InterruptedSupervisorHandoffKind
{
    None,
    SelectedSuccessor,
    RollbackSuccessor,
    ResumeCompatibilityRollback,
}

internal sealed record InterruptedSupervisorHandoffPlan(
    InterruptedSupervisorHandoffKind Kind,
    SupervisorSuccessorRequest? Request);

internal static class SupervisorInterruptedHandoffRecovery
{
    internal static InterruptedSupervisorHandoffPlan Inspect(
        string stateDirectory,
        string currentExecutable,
        DateTimeOffset nowUtc)
    {
        var current = AutomaticUpdateRunner.ResolveBuildIdentity(currentExecutable);
        if (current is null)
        {
            MarkInterrupted(
                stateDirectory,
                nowUtc,
                "The recovery supervisor executable identity is unavailable.");
            return None();
        }
        return Inspect(
            stateDirectory,
            new SupervisorExecutableIdentity(
                current.Version,
                Path.GetFullPath(currentExecutable),
                current.ExecutableSha256),
            nowUtc,
            CodexDesktopProcesses.Capture,
            SupervisorSuccessorAdmission.InspectPredecessor);
    }

    internal static InterruptedSupervisorHandoffPlan Inspect(
        string stateDirectory,
        SupervisorExecutableIdentity current,
        DateTimeOffset nowUtc,
        Func<CodexDesktopObservation>? captureDesktop = null,
        Func<int, DateTimeOffset, SupervisorPredecessorState>? inspectPredecessor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentNullException.ThrowIfNull(current);
        current.Validate();
        FileStream lifecycleLock;
        try
        {
            lifecycleLock = ContinuityLifecycleLock.Acquire(
                stateDirectory,
                timeout: TimeSpan.Zero);
        }
        catch (InvalidOperationException)
        {
            return None();
        }
        using (lifecycleLock)
        {
            return InspectOwned(
                stateDirectory,
                current,
                nowUtc,
                captureDesktop,
                inspectPredecessor);
        }
    }

    private static InterruptedSupervisorHandoffPlan InspectOwned(
        string stateDirectory,
        SupervisorExecutableIdentity current,
        DateTimeOffset nowUtc,
        Func<CodexDesktopObservation>? captureDesktop,
        Func<int, DateTimeOffset, SupervisorPredecessorState>? inspectPredecessor)
    {
        var store = new SupervisorSuccessorHandoffStore(
            ContinuityPaths.SupervisorHandoffFile(stateDirectory));
        var loaded = store.Load(nowUtc);
        if (loaded.Kind == SupervisorSuccessorHandoffLoadKind.Missing)
        {
            MarkInterrupted(
                stateDirectory,
                nowUtc,
                "The supervisor handoff is missing after an interrupted activation.");
            return None();
        }
        if (loaded.Kind == SupervisorSuccessorHandoffLoadKind.Expired)
        {
            MarkInterrupted(
                stateDirectory,
                nowUtc,
                "The supervisor handoff expired before activation could be verified.");
            store.Delete();
            return None();
        }
        if (loaded.Kind != SupervisorSuccessorHandoffLoadKind.Loaded || loaded.Handoff is null)
        {
            MarkInterrupted(
                stateDirectory,
                nowUtc,
                $"The supervisor handoff is {loaded.Kind.ToString().ToLowerInvariant()} after an interrupted activation.");
            return None();
        }

        var handoff = loaded.Handoff;
        inspectPredecessor ??= (_, _) => SupervisorPredecessorState.Exited;
        if (inspectPredecessor(
                handoff.PreviousSupervisorProcessId,
                handoff.PreviousSupervisorStartedAtUtc) != SupervisorPredecessorState.Exited)
        {
            return None();
        }
        var applyStatus = new ContinuityUpdateApplyStatusStore(
            ContinuityPaths.UpdateApplyStatusFile(stateDirectory)).Load();
        if (applyStatus is
            {
                Kind: ContinuityUpdateApplyLoadKind.Loaded,
                Status.State: ContinuityUpdateApplyStates.Active or
                    ContinuityUpdateApplyStates.RolledBack,
                Status.HandoffId: null,
            } && SameTarget(applyStatus.Status!, handoff.SelectedBuild))
        {
            store.Delete();
            return None();
        }
        if (captureDesktop is not null &&
            !SupervisorActivationSupport.DesktopAnchorStillRunning(
                handoff.DesktopProcesses,
                captureDesktop()))
        {
            MarkInterrupted(
                stateDirectory,
                nowUtc,
                "The original Codex desktop closed before interrupted activation proof could resume.");
            store.Delete();
            return None();
        }
        var lease = new BackendLeaseStore(
            ContinuityPaths.BackendLeaseFile(stateDirectory)).Load();
        if (lease.Kind != BackendLeaseLoadKind.Loaded || lease.Lease is null)
        {
            MarkInterrupted(
                stateDirectory,
                nowUtc,
                "The backend lease is unavailable after an interrupted activation.");
            return None();
        }
        if (lease.Lease != handoff.Backend)
        {
            var rebased = RebaseCrashedSuccessor(
                stateDirectory,
                handoff,
                lease.Lease,
                applyStatus,
                nowUtc);
            if (rebased is null)
            {
                MarkInterrupted(
                    stateDirectory,
                    nowUtc,
                    "The backend ownership changed without exact crashed-successor proof.");
                return None();
            }
            handoff = rebased;
        }
        if (SameBuild(current, handoff.RollbackBuild))
        {
            return new(
                InterruptedSupervisorHandoffKind.RollbackSuccessor,
                new(handoff.HandoffId, SupervisorSuccessorRole.Rollback));
        }
        if (SameBuild(current, handoff.SelectedBuild))
        {
            var resumingCompatibilityRollback =
                SameBuild(handoff.RunningBuild, handoff.SelectedBuild) &&
                !SameBuild(handoff.SelectedBuild, handoff.RollbackBuild);
            return new(
                resumingCompatibilityRollback
                    ? InterruptedSupervisorHandoffKind.ResumeCompatibilityRollback
                    : InterruptedSupervisorHandoffKind.SelectedSuccessor,
                new(handoff.HandoffId, SupervisorSuccessorRole.Selected));
        }

        MarkInterrupted(
            stateDirectory,
            nowUtc,
            "The available supervisor does not match the interrupted handoff builds.");
        return None();
    }

    private static void MarkInterrupted(
        string stateDirectory,
        DateTimeOffset nowUtc,
        string error)
    {
        var store = new ContinuityUpdateApplyStatusStore(
            ContinuityPaths.UpdateApplyStatusFile(stateDirectory));
        var loaded = store.Load();
        if (loaded is not
            {
                Kind: ContinuityUpdateApplyLoadKind.Loaded,
                Status.State: ContinuityUpdateApplyStates.Applying,
            })
        {
            return;
        }
        store.Save(loaded.Status! with
        {
            State = ContinuityUpdateApplyStates.Failed,
            UpdatedAtUtc = nowUtc,
            IdleSinceUtc = null,
            HandoffId = null,
            LastError = SupervisorActivationSupport.BoundError(error),
        });
    }

    private static SupervisorSuccessorHandoff? RebaseCrashedSuccessor(
        string stateDirectory,
        SupervisorSuccessorHandoff handoff,
        BackendLease lease,
        ContinuityUpdateApplyStatusLoadResult applyStatus,
        DateTimeOffset nowUtc)
    {
        if (!SameBackendExceptOwner(handoff.Backend, lease) ||
            applyStatus is not
            {
                Kind: ContinuityUpdateApplyLoadKind.Loaded,
                Status.State: ContinuityUpdateApplyStates.Applying,
            } ||
            applyStatus.Status!.HandoffId != handoff.HandoffId)
        {
            return null;
        }
        var supervisor = new SupervisorStatusStore(
            ContinuityPaths.SupervisorStatusFile(stateDirectory)).Load();
        if (supervisor is not
            {
                Kind: SupervisorStatusLoadKind.Loaded,
                Status.SupervisorStartedAtUtc: { } supervisorStartedAtUtc,
                Status.SupervisorExecutable: { } supervisorExecutable,
            } ||
            supervisor.Status!.SupervisorProcessId != lease.OwnerSupervisorProcessId ||
            supervisor.Status.BackendProcessId != lease.BackendProcessId ||
            supervisor.Status.Port != lease.PublicPort ||
            !SupervisorActivationSupport.SameOptionalPath(
                supervisor.Status.CodexHome,
                lease.CodexHome) ||
            !Path.GetFullPath(supervisorExecutable).Equals(
                Path.GetFullPath(handoff.SelectedBuild.Executable),
                StringComparison.OrdinalIgnoreCase) ||
            SupervisorSuccessorAdmission.InspectPredecessor(
                lease.OwnerSupervisorProcessId,
                supervisorStartedAtUtc) != SupervisorPredecessorState.Exited)
        {
            return null;
        }
        var selected = AutomaticUpdateRunner.ResolveBuildIdentity(
            handoff.SelectedBuild.Executable);
        if (selected is null ||
            !selected.Version.Equals(
                handoff.SelectedBuild.Version,
                StringComparison.OrdinalIgnoreCase) ||
            !selected.ExecutableSha256.Equals(
                handoff.SelectedBuild.ExecutableSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rebased = handoff with
        {
            HandoffId = Guid.NewGuid().ToString("N"),
            PreviousSupervisorProcessId = lease.OwnerSupervisorProcessId,
            PreviousSupervisorStartedAtUtc = supervisorStartedAtUtc,
            Backend = lease,
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc + SupervisorSuccessorHandoff.MaximumLifetime,
        };
        rebased.Validate();
        new SupervisorSuccessorHandoffStore(
            ContinuityPaths.SupervisorHandoffFile(stateDirectory)).Write(rebased);
        new ContinuityUpdateApplyStatusStore(
            ContinuityPaths.UpdateApplyStatusFile(stateDirectory)).Save(
                applyStatus.Status with
                {
                    UpdatedAtUtc = nowUtc,
                    HandoffId = rebased.HandoffId,
                    LastError = null,
                });
        return rebased;
    }

    private static InterruptedSupervisorHandoffPlan None() => new(
        InterruptedSupervisorHandoffKind.None,
        Request: null);

    private static bool SameBuild(
        SupervisorExecutableIdentity left,
        SupervisorExecutableIdentity right) =>
        Path.GetFullPath(left.Executable).Equals(
            Path.GetFullPath(right.Executable),
            StringComparison.OrdinalIgnoreCase) &&
        left.Version.Equals(right.Version, StringComparison.OrdinalIgnoreCase) &&
        left.ExecutableSha256.Equals(
            right.ExecutableSha256,
            StringComparison.OrdinalIgnoreCase);

    private static bool SameTarget(
        ContinuityUpdateApplyStatus status,
        SupervisorExecutableIdentity build) =>
        status.TargetVersion?.Equals(build.Version, StringComparison.OrdinalIgnoreCase) == true &&
        status.TargetExecutableSha256?.Equals(
            build.ExecutableSha256,
            StringComparison.OrdinalIgnoreCase) == true;

    private static bool SameBackendExceptOwner(BackendLease left, BackendLease right) =>
        left.SchemaVersion == right.SchemaVersion &&
        left.BackendProcessId == right.BackendProcessId &&
        left.PublicPort == right.PublicPort &&
        left.BackendPort == right.BackendPort &&
        Path.GetFullPath(left.BackendExecutable).Equals(
            Path.GetFullPath(right.BackendExecutable),
            StringComparison.OrdinalIgnoreCase) &&
        SupervisorActivationSupport.SameOptionalPath(left.CodexHome, right.CodexHome) &&
        left.BackendStartedAtUtc == right.BackendStartedAtUtc;

}

internal sealed class SupervisorUpdateApplyMonitor
{
    internal static readonly TimeSpan ObservationInterval = TimeSpan.FromSeconds(5);

    private readonly string stateDirectory;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<string, int, int, CancellationToken, Task<ContinuityHandoffPlan>>
        observePlan;
    private readonly Func<int, int, CancellationToken, Task<IReadOnlyList<string>>> readThreadIds;
    private readonly Func<CodexDesktopObservation> captureDesktop;
    private readonly Func<
        BackendLease,
        IReadOnlyList<string>,
        IReadOnlyList<CodexDesktopProcessIdentity>,
        DateTimeOffset,
        SupervisorSuccessorHandoff> createHandoff;
    private readonly Action<SupervisorSuccessorHandoff> launchSuccessor;
    private readonly TimeSpan stableIdleWindow;
    private DateTimeOffset nextObservationAtUtc;

    internal SupervisorUpdateApplyMonitor(
        string stateDirectory,
        Func<DateTimeOffset>? utcNow = null,
        Func<string, int, int, CancellationToken, Task<ContinuityHandoffPlan>>? observePlan = null,
        Func<int, int, CancellationToken, Task<IReadOnlyList<string>>>? readThreadIds = null,
        Func<CodexDesktopObservation>? captureDesktop = null,
        Func<
            BackendLease,
            IReadOnlyList<string>,
            IReadOnlyList<CodexDesktopProcessIdentity>,
            DateTimeOffset,
            SupervisorSuccessorHandoff>? createHandoff = null,
        Action<SupervisorSuccessorHandoff>? launchSuccessor = null,
        TimeSpan? stableIdleWindow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        this.stateDirectory = stateDirectory;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.observePlan = observePlan ?? ((directory, port, processId, token) =>
            PrivateBackendHandoffObserver.ObserveAsync(
                directory,
                port,
                processId,
                token));
        this.readThreadIds = readThreadIds ?? SupervisorActivationSupport.ReadOwnedThreadIdsAsync;
        this.captureDesktop = captureDesktop ?? CodexDesktopProcesses.Capture;
        this.createHandoff = createHandoff ?? ((backend, threadIds, desktopProcesses, now) =>
            SupervisorUpdateHandoffFactory.CreateFromInstalledState(
                stateDirectory,
                backend.PublicPort,
                backend.CodexHome,
                backend,
                threadIds,
                desktopProcesses,
                now));
        this.launchSuccessor = launchSuccessor ?? LaunchSuccessor;
        this.stableIdleWindow = stableIdleWindow ?? ContinuityUpdateApplyPlanner.StableIdleWindow;
    }

    internal async Task<bool> TryLaunchAsync(
        LoopbackRelay relay,
        BackendLease backend,
        int backendPort,
        int backendProcessId,
        CancellationToken cancellationToken)
    {
        var now = utcNow();
        if (now < nextObservationAtUtc)
        {
            return false;
        }
        nextObservationAtUtc = now + ObservationInterval;

        var policyStore = new ContinuityUpdateApplyPolicyStore(
            ContinuityPaths.UpdateApplyPolicyFile(stateDirectory));
        var statusStore = new ContinuityUpdateApplyStatusStore(
            ContinuityPaths.UpdateApplyStatusFile(stateDirectory));
        var policy = policyStore.Load();
        var status = statusStore.Load();
        var selected = ContinuitySelectedBuildReader.Load(stateDirectory);
        var selectedBuild = selected.Kind == ContinuitySelectedBuildLoadKind.Loaded
            ? selected.Build
            : null;
        ContinuityHandoffPlan plan;
        if (policy is not
            {
                Kind: ContinuityUpdateApplyLoadKind.Loaded,
                Policy.AutomaticApplyWhenIdle: true,
            })
        {
            plan = UnavailablePlan("automaticApplyDisabled");
        }
        else try
            {
                plan = await observePlan(
                    stateDirectory,
                    backendPort,
                    backendProcessId,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                SupervisorActivationSupport.IsExpectedFailure(exception))
            {
                plan = UnavailablePlan($"observationFailed:{exception.GetType().Name}");
            }

        var decision = ContinuityUpdateApplyPlanner.Evaluate(
            policy,
            status,
            plan,
            selectedBuild,
            now,
            stableIdleWindow);
        if (decision.PersistStatus && ShouldPersist(status, decision.Status))
        {
            statusStore.Save(decision.Status);
        }
        if (!decision.BeginApply)
        {
            return false;
        }

        FileStream? lifecycleLock = null;
        try
        {
            lifecycleLock = ContinuityLifecycleLock.Acquire(
                stateDirectory,
                timeout: TimeSpan.Zero);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        using (lifecycleLock)
        {
            return await LaunchBehindGateAsync(
                relay,
                backend,
                backendPort,
                backendProcessId,
                decision.Status,
                statusStore,
                cancellationToken);
        }
    }

    private async Task<bool> LaunchBehindGateAsync(
        LoopbackRelay relay,
        BackendLease backend,
        int backendPort,
        int backendProcessId,
        ContinuityUpdateApplyStatus waitingStatus,
        ContinuityUpdateApplyStatusStore statusStore,
        CancellationToken cancellationToken)
    {
        RelayGateLease? gate = null;
        var launched = false;
        var manifestWritten = false;
        var handoffStore = new SupervisorSuccessorHandoffStore(
            ContinuityPaths.SupervisorHandoffFile(stateDirectory));
        try
        {
            var gated = await GatedHandoffTransition.CloseAndRecomputeAsync(
                relay,
                token => observePlan(
                    stateDirectory,
                    backendPort,
                    backendProcessId,
                    token));
            gate = gated.GateLease;
            if (gate is null || !gated.Plan.TransitionReady ||
                !gated.Plan.PendingUpdate || gated.Plan.Action != "applyUpdate")
            {
                statusStore.Save(waitingStatus with
                {
                    UpdatedAtUtc = utcNow(),
                    IdleSinceUtc = null,
                });
                return false;
            }

            var persistedThreadIds = await readThreadIds(
                backendPort,
                backendProcessId,
                cancellationToken);
            var desktop = captureDesktop();
            if (desktop.Kind != CodexDesktopObservationKind.Running ||
                desktop.Processes.Count == 0)
            {
                throw new InvalidOperationException(
                    "The running Codex desktop identity could not be preserved for update apply.");
            }
            var desktopAnchor = desktop.Processes
                .OrderBy(process => process.StartedAtUtcTicks)
                .ThenBy(process => process.ProcessId)
                .First();
            var persistedLease = new BackendLeaseStore(
                ContinuityPaths.BackendLeaseFile(stateDirectory)).Load();
            if (persistedLease.Kind != BackendLeaseLoadKind.Loaded ||
                persistedLease.Lease != backend)
            {
                throw new InvalidDataException(
                    "The backend lease changed before update apply.");
            }

            var handoff = createHandoff(
                backend,
                persistedThreadIds,
                [desktopAnchor],
                utcNow());
            handoffStore.Write(handoff);
            manifestWritten = true;
            statusStore.Save(waitingStatus with
            {
                State = ContinuityUpdateApplyStates.Applying,
                UpdatedAtUtc = utcNow(),
                IdleSinceUtc = null,
                HandoffId = handoff.HandoffId,
                LastError = null,
            });
            launchSuccessor(handoff);
            launched = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            SupervisorActivationSupport.IsExpectedFailure(exception))
        {
            statusStore.Save(waitingStatus with
            {
                State = ContinuityUpdateApplyStates.Failed,
                UpdatedAtUtc = utcNow(),
                IdleSinceUtc = null,
                HandoffId = null,
                LastError = SupervisorActivationSupport.BoundError(exception.Message),
            });
            return false;
        }
        finally
        {
            if (!launched)
            {
                if (manifestWritten)
                {
                    handoffStore.Delete();
                }
                if (gate is not null && !gate.TryOpen())
                {
                    throw new InvalidOperationException(
                        "The update apply relay gate could not be reopened safely.");
                }
            }
        }
    }

    private static void LaunchSuccessor(SupervisorSuccessorHandoff handoff)
    {
        using var process = DetachedProcessLauncher.Start(
            handoff.SelectedBuild.Executable,
            [
                "serve",
                "--port",
                handoff.PublicPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--successor-handoff",
                handoff.HandoffId,
                "--successor-role",
                "selected",
            ],
            Path.GetDirectoryName(handoff.SelectedBuild.Executable)
                ?? throw new InvalidOperationException(
                    "The selected supervisor has no working directory."));
    }

    private static bool ShouldPersist(
        ContinuityUpdateApplyStatusLoadResult existing,
        ContinuityUpdateApplyStatus next) => existing.Kind != ContinuityUpdateApplyLoadKind.Loaded ||
        existing.Status is not { } current ||
        current.State != next.State ||
        current.PolicyGeneration != next.PolicyGeneration ||
        current.TargetVersion != next.TargetVersion ||
        current.TargetExecutableSha256 != next.TargetExecutableSha256 ||
        current.IdleSinceUtc != next.IdleSinceUtc ||
        current.HandoffId != next.HandoffId ||
        current.LastError != next.LastError;

    private static ContinuityHandoffPlan UnavailablePlan(string reason) => new(
        "wait",
        TransitionReady: false,
        BackendReady: false,
        "unknown",
        PendingUpdate: false,
        ThreadCount: 0,
        new(0, 0, 0, 0, 0),
        [reason]);

}

internal enum SupervisorSuccessorCompletionKind
{
    Continue,
    RollbackLaunched,
}

internal static class SupervisorSuccessorCompletion
{
    internal static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan ReconnectPollInterval = TimeSpan.FromMilliseconds(100);

    internal static async Task<SupervisorSuccessorCompletionKind> CompleteAsync(
        string stateDirectory,
        AdmittedSupervisorSuccessor successor,
        LoopbackRelay relay,
        BackendLease backend,
        int backendPort,
        int backendProcessId,
        CancellationToken cancellationToken,
        Func<int, int, CancellationToken, Task<IReadOnlyList<string>>>? readThreadIds = null,
        Func<CodexDesktopObservation>? captureDesktop = null,
        Func<int>? activeConnections = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<
            SupervisorSuccessorHandoff,
            BackendLease,
            DateTimeOffset,
            SupervisorSuccessorHandoff>? createRollback = null,
        Action<SupervisorSuccessorHandoff>? launchRollback = null)
    {
        readThreadIds ??= SupervisorActivationSupport.ReadOwnedThreadIdsAsync;
        captureDesktop ??= CodexDesktopProcesses.Capture;
        activeConnections ??= () => relay.ActiveConnectionCount;
        utcNow ??= () => DateTimeOffset.UtcNow;
        delay ??= Task.Delay;
        createRollback ??= (original, currentBackend, now) =>
            SupervisorRollbackHandoffFactory.Create(
                original,
                currentBackend,
                Environment.ProcessId,
                Program.ProcessStartedAtUtcForHandoff,
                now);
        launchRollback ??= LaunchRollback;
        var statusStore = new ContinuityUpdateApplyStatusStore(
            ContinuityPaths.UpdateApplyStatusFile(stateDirectory));
        var handoffStore = new SupervisorSuccessorHandoffStore(
            ContinuityPaths.SupervisorHandoffFile(stateDirectory));
        try
        {
            await VerifyAsync(
                successor.Handoff,
                backendPort,
                backendProcessId,
                readThreadIds,
                captureDesktop,
                activeConnections,
                utcNow,
                delay,
                cancellationToken);
            var previous = statusStore.Load();
            var generation = previous.Kind == ContinuityUpdateApplyLoadKind.Loaded
                ? previous.Status!.PolicyGeneration
                : 0;
            var previousError = previous.Kind == ContinuityUpdateApplyLoadKind.Loaded
                ? previous.Status!.LastError
                : null;
            statusStore.Save(new(
                ContinuityUpdateApplyStatus.CurrentSchemaVersion,
                successor.Role == SupervisorSuccessorRole.Selected
                    ? ContinuityUpdateApplyStates.Active
                    : ContinuityUpdateApplyStates.RolledBack,
                generation,
                successor.Handoff.SelectedBuild.Version,
                successor.Handoff.SelectedBuild.ExecutableSha256,
                utcNow(),
                IdleSinceUtc: null,
                HandoffId: null,
                LastError: successor.Role == SupervisorSuccessorRole.Rollback
                    ? previousError ?? "The selected supervisor failed verification."
                    : null));
            handoffStore.Delete();
            return SupervisorSuccessorCompletionKind.Continue;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (SupervisorActivationSupport.IsExpectedFailure(exception))
        {
            if (successor.Role == SupervisorSuccessorRole.Rollback)
            {
                SaveFailure(statusStore, successor.Handoff, utcNow(), exception.Message);
                return SupervisorSuccessorCompletionKind.Continue;
            }
            return await TryLaunchRollbackAsync(
                stateDirectory,
                successor.Handoff,
                relay,
                backend,
                statusStore,
                handoffStore,
                utcNow,
                createRollback,
                launchRollback,
                exception.Message);
        }
    }

    private static async Task VerifyAsync(
        SupervisorSuccessorHandoff handoff,
        int backendPort,
        int backendProcessId,
        Func<int, int, CancellationToken, Task<IReadOnlyList<string>>> readThreadIds,
        Func<CodexDesktopObservation> captureDesktop,
        Func<int> activeConnections,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        var currentThreadIds = await readThreadIds(
            backendPort,
            backendProcessId,
            cancellationToken);
        if (handoff.PersistedThreadIds.Except(currentThreadIds, StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException(
                "Persisted thread IDs were not all readable after the supervisor handoff.");
        }
        if (!SupervisorActivationSupport.DesktopAnchorStillRunning(
                handoff.DesktopProcesses,
                captureDesktop()))
        {
            throw new InvalidOperationException(
                "The original Codex desktop process did not remain running through the handoff.");
        }

        var deadline = utcNow() + ReconnectTimeout;
        while (activeConnections() <= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SupervisorActivationSupport.DesktopAnchorStillRunning(
                    handoff.DesktopProcesses,
                    captureDesktop()))
            {
                throw new InvalidOperationException(
                    "The original Codex desktop process exited before reconnecting.");
            }
            if (utcNow() >= deadline)
            {
                throw new TimeoutException(
                    "The running Codex desktop did not reconnect within the bounded window.");
            }
            await delay(ReconnectPollInterval, cancellationToken);
        }
    }

    private static async Task<SupervisorSuccessorCompletionKind> TryLaunchRollbackAsync(
        string stateDirectory,
        SupervisorSuccessorHandoff original,
        LoopbackRelay relay,
        BackendLease backend,
        ContinuityUpdateApplyStatusStore statusStore,
        SupervisorSuccessorHandoffStore handoffStore,
        Func<DateTimeOffset> utcNow,
        Func<
            SupervisorSuccessorHandoff,
            BackendLease,
            DateTimeOffset,
            SupervisorSuccessorHandoff> createRollback,
        Action<SupervisorSuccessorHandoff> launchRollback,
        string verificationError)
    {
        RelayGateLease? gate = null;
        var launched = false;
        try
        {
            gate = await relay.CloseGateExclusivelyAsync();
            var rollback = createRollback(original, backend, utcNow());
            handoffStore.Write(rollback);
            var previous = statusStore.Load();
            var generation = previous.Kind == ContinuityUpdateApplyLoadKind.Loaded
                ? previous.Status!.PolicyGeneration
                : 0;
            statusStore.Save(new(
                ContinuityUpdateApplyStatus.CurrentSchemaVersion,
                ContinuityUpdateApplyStates.Applying,
                generation,
                original.SelectedBuild.Version,
                original.SelectedBuild.ExecutableSha256,
                utcNow(),
                IdleSinceUtc: null,
                rollback.HandoffId,
                SupervisorActivationSupport.BoundError(verificationError)));
            launchRollback(rollback);
            launched = true;
            return SupervisorSuccessorCompletionKind.RollbackLaunched;
        }
        catch (Exception exception) when (SupervisorActivationSupport.IsExpectedFailure(exception))
        {
            handoffStore.Delete();
            SaveFailure(
                statusStore,
                original,
                utcNow(),
                $"{verificationError} Rollback launch failed: {exception.Message}");
            return SupervisorSuccessorCompletionKind.Continue;
        }
        finally
        {
            if (!launched && gate is not null && !gate.TryOpen())
            {
                throw new InvalidOperationException(
                    "The rollback relay gate could not be reopened safely.");
            }
        }
    }

    private static void SaveFailure(
        ContinuityUpdateApplyStatusStore store,
        SupervisorSuccessorHandoff handoff,
        DateTimeOffset now,
        string error)
    {
        var previous = store.Load();
        var generation = previous.Kind == ContinuityUpdateApplyLoadKind.Loaded
            ? previous.Status!.PolicyGeneration
            : 0;
        store.Save(new(
            ContinuityUpdateApplyStatus.CurrentSchemaVersion,
            ContinuityUpdateApplyStates.Failed,
            generation,
            handoff.SelectedBuild.Version,
            handoff.SelectedBuild.ExecutableSha256,
            now,
            IdleSinceUtc: null,
            HandoffId: null,
            SupervisorActivationSupport.BoundError(error)));
    }

    private static void LaunchRollback(SupervisorSuccessorHandoff handoff)
    {
        using var process = DetachedProcessLauncher.Start(
            handoff.SelectedBuild.Executable,
            [
                "rollback-helper",
                "--port",
                handoff.PublicPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--successor-handoff",
                handoff.HandoffId,
                "--successor-role",
                "selected",
            ],
            Path.GetDirectoryName(handoff.SelectedBuild.Executable)
                ?? throw new InvalidOperationException(
                    "The rollback helper has no working directory."));
    }
}

internal static class SupervisorRollbackHelper
{
    internal static async Task<int> CompleteAsync(
        string stateDirectory,
        SupervisorSuccessorHandoff handoff,
        Func<SupervisorSuccessorHandoff, CancellationToken, Task<bool>> activateRollback,
        Func<int, CancellationToken, Task<IReadOnlyList<string>>> readThreadIds,
        Func<CodexDesktopObservation> captureDesktop,
        Func<DateTimeOffset> utcNow,
        CancellationToken cancellationToken)
    {
        var statusStore = new ContinuityUpdateApplyStatusStore(
            ContinuityPaths.UpdateApplyStatusFile(stateDirectory));
        var handoffStore = new SupervisorSuccessorHandoffStore(
            ContinuityPaths.SupervisorHandoffFile(stateDirectory));
        try
        {
            if (!await activateRollback(handoff, cancellationToken))
            {
                throw new InvalidOperationException(
                    "The previous known-good supervisor did not become ready.");
            }
            var threadIds = await readThreadIds(handoff.PublicPort, cancellationToken);
            if (handoff.PersistedThreadIds.Except(threadIds, StringComparer.Ordinal).Any())
            {
                throw new InvalidDataException(
                    "Persisted thread IDs were not all readable after rollback.");
            }
            var desktop = captureDesktop();
            if (desktop.Kind != CodexDesktopObservationKind.Running ||
                handoff.DesktopProcesses.Any(expected => !desktop.Processes.Contains(expected)))
            {
                throw new InvalidOperationException(
                    "The original Codex desktop process did not remain running through rollback.");
            }

            var previous = statusStore.Load();
            var generation = previous.Kind == ContinuityUpdateApplyLoadKind.Loaded
                ? previous.Status!.PolicyGeneration
                : 0;
            var error = previous.Kind == ContinuityUpdateApplyLoadKind.Loaded
                ? previous.Status!.LastError
                : null;
            statusStore.Save(new(
                ContinuityUpdateApplyStatus.CurrentSchemaVersion,
                ContinuityUpdateApplyStates.RolledBack,
                generation,
                handoff.SelectedBuild.Version,
                handoff.SelectedBuild.ExecutableSha256,
                utcNow(),
                IdleSinceUtc: null,
                HandoffId: null,
                error ?? "The selected supervisor failed verification."));
            handoffStore.Delete();
            return 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (SupervisorActivationSupport.IsExpectedFailure(exception))
        {
            var previous = statusStore.Load();
            var generation = previous.Kind == ContinuityUpdateApplyLoadKind.Loaded
                ? previous.Status!.PolicyGeneration
                : 0;
            statusStore.Save(new(
                ContinuityUpdateApplyStatus.CurrentSchemaVersion,
                ContinuityUpdateApplyStates.Failed,
                generation,
                handoff.SelectedBuild.Version,
                handoff.SelectedBuild.ExecutableSha256,
                utcNow(),
                IdleSinceUtc: null,
                HandoffId: null,
                SupervisorActivationSupport.BoundError(exception.Message)));
            return 1;
        }
    }
}

internal static class SupervisorRollbackHandoffFactory
{
    internal static SupervisorSuccessorHandoff Create(
        SupervisorSuccessorHandoff original,
        BackendLease backend,
        int supervisorProcessId,
        DateTimeOffset supervisorStartedAtUtc,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(backend);
        var currentExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The selected successor executable path is unavailable.");
        var currentBuild = AutomaticUpdateRunner.ResolveBuildIdentity(currentExecutable)
            ?? throw new InvalidDataException(
                "The selected successor executable identity is unavailable.");
        if (!Path.GetFullPath(currentExecutable).Equals(
                Path.GetFullPath(original.SelectedBuild.Executable),
                StringComparison.OrdinalIgnoreCase) ||
            !currentBuild.Version.Equals(
                original.SelectedBuild.Version,
                StringComparison.OrdinalIgnoreCase) ||
            !currentBuild.ExecutableSha256.Equals(
                original.SelectedBuild.ExecutableSha256,
                StringComparison.OrdinalIgnoreCase) ||
            backend.OwnerSupervisorProcessId != supervisorProcessId ||
            backend.PublicPort != original.PublicPort ||
            !SupervisorActivationSupport.SameOptionalPath(
                backend.CodexHome,
                original.CodexHome))
        {
            throw new InvalidDataException(
                "The selected successor cannot prove the rollback handoff identity.");
        }
        var rollback = new SupervisorSuccessorHandoff(
            SupervisorSuccessorHandoff.CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"),
            supervisorProcessId,
            supervisorStartedAtUtc,
            original.PublicPort,
            original.CodexHome,
            original.SelectedBuild,
            original.SelectedBuild,
            original.RollbackBuild,
            backend,
            original.PersistedThreadIds,
            original.DesktopProcesses,
            nowUtc,
            nowUtc + SupervisorSuccessorHandoff.MaximumLifetime);
        rollback.Validate();
        return rollback;
    }

}
