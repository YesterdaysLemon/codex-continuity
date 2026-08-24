namespace CodexContinuity;

internal sealed record ContinuityUpdateApplyDecision(
    ContinuityUpdateApplyStatus Status,
    bool BeginApply,
    bool PersistStatus = true);

internal static class ContinuityUpdateApplyPlanner
{
    internal static readonly TimeSpan StableIdleWindow = TimeSpan.FromSeconds(30);

    internal static ContinuityUpdateApplyDecision Evaluate(
        ContinuityUpdateApplyPolicyLoadResult policyResult,
        ContinuityUpdateApplyStatusLoadResult statusResult,
        ContinuityHandoffPlan plan,
        ContinuityBuildIdentity? selectedBuild,
        DateTimeOffset nowUtc,
        TimeSpan? stableIdleWindow = null)
    {
        ArgumentNullException.ThrowIfNull(policyResult);
        ArgumentNullException.ThrowIfNull(statusResult);
        ArgumentNullException.ThrowIfNull(plan);
        var idleWindow = stableIdleWindow ?? StableIdleWindow;
        if (idleWindow <= TimeSpan.Zero || idleWindow > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(stableIdleWindow));
        }

        if (policyResult.Kind == ContinuityUpdateApplyLoadKind.Missing)
        {
            return new(Status(
                ContinuityUpdateApplyStates.StagedOnly,
                generation: 0,
                selectedBuild,
                nowUtc), BeginApply: false);
        }
        if (policyResult.Kind != ContinuityUpdateApplyLoadKind.Loaded ||
            policyResult.Policy is null)
        {
            return new(Status(
                ContinuityUpdateApplyStates.Failed,
                generation: 0,
                selectedBuild,
                nowUtc,
                lastError: $"Update apply policy is {Name(policyResult.Kind)}."),
                BeginApply: false,
                PersistStatus: false);
        }

        var policy = policyResult.Policy;
        if (!policy.AutomaticApplyWhenIdle)
        {
            return new(Status(
                ContinuityUpdateApplyStates.StagedOnly,
                policy.Generation,
                selectedBuild,
                nowUtc), false);
        }

        if (statusResult.Kind is not (
                ContinuityUpdateApplyLoadKind.Missing or
                ContinuityUpdateApplyLoadKind.Loaded))
        {
            return new(Status(
                ContinuityUpdateApplyStates.Failed,
                policy.Generation,
                selectedBuild,
                nowUtc,
                lastError: $"Update apply status is {Name(statusResult.Kind)}."),
                BeginApply: false,
                PersistStatus: false);
        }

        var previous = statusResult.Kind == ContinuityUpdateApplyLoadKind.Loaded
            ? statusResult.Status
            : null;
        if (previous is not null &&
            previous.PolicyGeneration == policy.Generation &&
            previous.State is (
                ContinuityUpdateApplyStates.Failed or
                ContinuityUpdateApplyStates.RolledBack) &&
            SameTarget(previous, selectedBuild))
        {
            return new(previous, BeginApply: false, PersistStatus: false);
        }

        var ready = selectedBuild is not null &&
            plan.TransitionReady &&
            plan.PendingUpdate &&
            plan.Action == "applyUpdate";
        if (!ready)
        {
            return new(Status(
                ContinuityUpdateApplyStates.Waiting,
                policy.Generation,
                selectedBuild,
                nowUtc), BeginApply: false);
        }

        var idleSince = previous is
        {
            State: ContinuityUpdateApplyStates.Waiting,
            IdleSinceUtc: { } previousIdleSince,
        } &&
            previous.PolicyGeneration == policy.Generation &&
            SameTarget(previous, selectedBuild)
                ? previousIdleSince
                : nowUtc;
        var waiting = Status(
            ContinuityUpdateApplyStates.Waiting,
            policy.Generation,
            selectedBuild,
            nowUtc,
            idleSince);
        return new(waiting, nowUtc - idleSince >= idleWindow);
    }

    private static ContinuityUpdateApplyStatus Status(
        string state,
        long generation,
        ContinuityBuildIdentity? target,
        DateTimeOffset nowUtc,
        DateTimeOffset? idleSinceUtc = null,
        string? lastError = null) => new(
            ContinuityUpdateApplyStatus.CurrentSchemaVersion,
            state,
            generation,
            target?.Version,
            target?.ExecutableSha256,
            nowUtc,
            idleSinceUtc,
            HandoffId: null,
            lastError);

    private static bool SameTarget(
        ContinuityUpdateApplyStatus status,
        ContinuityBuildIdentity? target) => target is not null &&
        status.TargetVersion?.Equals(target.Version, StringComparison.OrdinalIgnoreCase) == true &&
        status.TargetExecutableSha256?.Equals(
            target.ExecutableSha256,
            StringComparison.OrdinalIgnoreCase) == true;

    private static string Name(ContinuityUpdateApplyLoadKind kind) =>
        kind.ToString().ToLowerInvariant();
}

internal static class SupervisorUpdateHandoffFactory
{
    internal static SupervisorSuccessorHandoff CreateFromInstalledState(
        string stateDirectory,
        int publicPort,
        string? codexHome,
        BackendLease backend,
        IReadOnlyList<string> persistedThreadIds,
        IReadOnlyList<CodexDesktopProcessIdentity> desktopProcesses,
        DateTimeOffset nowUtc)
    {
        var currentExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "The running supervisor executable path is unavailable.");
        var installState = new InstallStateStore(
            ContinuityPaths.InstallStateFile(stateDirectory)).Load()
            ?? throw new InvalidDataException("Installed Continuity state is missing.");
        var running = ResolveExecutable(currentExecutable, "running");
        var selected = ResolveExecutable(installState.InstalledExecutable, "selected");
        var rollbackExecutable = installState.PreviousInstalledExecutable
            ?? throw new InvalidDataException("No previous supervisor is available for rollback.");
        var rollback = ResolveExecutable(rollbackExecutable, "rollback");
        var updateState = new ContinuityUpdateStateStore(
            ContinuityPaths.UpdateStatusFile(stateDirectory)).Load();
        return Create(
            installState,
            updateState,
            running,
            selected,
            rollback,
            publicPort,
            codexHome,
            backend,
            persistedThreadIds,
            desktopProcesses,
            Environment.ProcessId,
            Program.ProcessStartedAtUtcForHandoff,
            nowUtc);
    }

    internal static SupervisorSuccessorHandoff Create(
        InstallState installState,
        ContinuityUpdateStateLoadResult updateState,
        SupervisorExecutableIdentity running,
        SupervisorExecutableIdentity selected,
        SupervisorExecutableIdentity rollback,
        int publicPort,
        string? codexHome,
        BackendLease backend,
        IReadOnlyList<string> persistedThreadIds,
        IReadOnlyList<CodexDesktopProcessIdentity> desktopProcesses,
        int supervisorProcessId,
        DateTimeOffset supervisorStartedAtUtc,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(installState);
        ArgumentNullException.ThrowIfNull(updateState);
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(rollback);
        ArgumentNullException.ThrowIfNull(backend);
        running.Validate();
        selected.Validate();
        rollback.Validate();
        LoopbackEndpoint.ValidatePort(publicPort);

        if (installState.SchemaVersion != InstallStateStore.CurrentSchemaVersion ||
            installState.Lifecycle != InstallLifecycle.Installed ||
            installState.Port != publicPort ||
            !SamePath(installState.InstalledExecutable, selected.Executable) ||
            !installState.BinarySha256.Equals(
                selected.ExecutableSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The selected supervisor does not match installed Continuity state.");
        }
        if (installState.PreviousInstalledExecutable is null ||
            !SamePath(installState.PreviousInstalledExecutable, rollback.Executable) ||
            !SameIdentity(running, rollback))
        {
            throw new InvalidDataException(
                "The running supervisor is not the exact installed rollback build.");
        }
        if (SameIdentity(running, selected))
        {
            throw new InvalidDataException("No different staged supervisor is selected.");
        }

        var selectedBuild = new ContinuityBuildIdentity(
            selected.Version,
            selected.ExecutableSha256);
        var plan = ContinuityHandoffPlanner.Create(
            backendReady: true,
            threads: [],
            updateState,
            new(ContinuitySelectedBuildLoadKind.Loaded, selectedBuild));
        var ledger = updateState.State;
        var selectedRelease = ledger?.Releases.FirstOrDefault(release =>
            release.Version.Equals(selected.Version, StringComparison.OrdinalIgnoreCase));
        if (!plan.TransitionReady || !plan.PendingUpdate || plan.Action != "applyUpdate" ||
            ledger is null ||
            !ledger.RunningVersion.Equals(running.Version, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                ledger.RunningExecutableSha256,
                running.ExecutableSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                selectedRelease?.RollbackExecutableSha256,
                rollback.ExecutableSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The staged update ledger does not prove the running, selected, and rollback builds.");
        }
        if (supervisorProcessId <= 0 ||
            backend.OwnerSupervisorProcessId != supervisorProcessId ||
            backend.PublicPort != publicPort ||
            !SameOptionalPath(backend.CodexHome, codexHome))
        {
            throw new InvalidDataException(
                "The backend lease does not belong to the running supervisor installation.");
        }

        var handoff = new SupervisorSuccessorHandoff(
            SupervisorSuccessorHandoff.CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"),
            supervisorProcessId,
            supervisorStartedAtUtc,
            publicPort,
            codexHome,
            running,
            selected,
            rollback,
            backend,
            persistedThreadIds,
            desktopProcesses,
            nowUtc,
            nowUtc + SupervisorSuccessorHandoff.MaximumLifetime);
        handoff.Validate();
        return handoff;
    }

    private static SupervisorExecutableIdentity ResolveExecutable(
        string executable,
        string role)
    {
        var build = AutomaticUpdateRunner.ResolveBuildIdentity(executable)
            ?? throw new InvalidDataException(
                $"The {role} supervisor executable identity is unavailable.");
        return new(build.Version, Path.GetFullPath(executable), build.ExecutableSha256);
    }

    private static bool SameIdentity(
        SupervisorExecutableIdentity left,
        SupervisorExecutableIdentity right) =>
        SamePath(left.Executable, right.Executable) &&
        left.Version.Equals(right.Version, StringComparison.OrdinalIgnoreCase) &&
        left.ExecutableSha256.Equals(right.ExecutableSha256, StringComparison.OrdinalIgnoreCase);

    private static bool SameOptionalPath(string? left, string? right) =>
        left is null && right is null ||
        left is not null && right is not null && SamePath(left, right);

    private static bool SamePath(string left, string right) => Path.GetFullPath(left).Equals(
        Path.GetFullPath(right),
        StringComparison.OrdinalIgnoreCase);
}
