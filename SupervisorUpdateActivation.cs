using System.Diagnostics;

namespace CodexContinuity;

internal sealed record AdmittedSupervisorSuccessor(
    SupervisorSuccessorHandoff Handoff,
    SupervisorSuccessorRole Role);

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
        this.readThreadIds = readThreadIds ?? ReadThreadIdsAsync;
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
            catch (Exception exception) when (IsExpectedFailure(exception))
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
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            statusStore.Save(waitingStatus with
            {
                State = ContinuityUpdateApplyStates.Failed,
                UpdatedAtUtc = utcNow(),
                IdleSinceUtc = null,
                HandoffId = null,
                LastError = BoundError(exception.Message),
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

    private static async Task<IReadOnlyList<string>> ReadThreadIdsAsync(
        int backendPort,
        int backendProcessId,
        CancellationToken cancellationToken)
    {
        await using var client = await Program.RpcClient.ConnectOwnedAsync(
            LoopbackEndpoint.WebSocketUrl(backendPort),
            backendProcessId,
            cancellationToken);
        return await client.ListOwnedThreadIdsAsync(cancellationToken);
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

    internal static bool IsExpectedFailure(Exception exception) => exception is
        ArgumentException or IOException or InvalidDataException or InvalidOperationException or
        NotSupportedException or System.Text.Json.JsonException or TimeoutException or
        UnauthorizedAccessException or System.ComponentModel.Win32Exception or
        System.Net.Http.HttpRequestException or
        System.Net.WebSockets.WebSocketException;

    internal static string BoundError(string error)
    {
        const int maximumLength = 2048;
        var singleLine = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= maximumLength
            ? singleLine
            : $"{singleLine[..(maximumLength - 1)]}…";
    }
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
        readThreadIds ??= ReadThreadIdsAsync;
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
        catch (Exception exception) when (SupervisorUpdateApplyMonitor.IsExpectedFailure(exception))
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
        if (!DesktopAnchorStillRunning(handoff.DesktopProcesses, captureDesktop()))
        {
            throw new InvalidOperationException(
                "The original Codex desktop process did not remain running through the handoff.");
        }

        var deadline = utcNow() + ReconnectTimeout;
        while (activeConnections() <= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DesktopAnchorStillRunning(handoff.DesktopProcesses, captureDesktop()))
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
                SupervisorUpdateApplyMonitor.BoundError(verificationError)));
            launchRollback(rollback);
            launched = true;
            return SupervisorSuccessorCompletionKind.RollbackLaunched;
        }
        catch (Exception exception) when (SupervisorUpdateApplyMonitor.IsExpectedFailure(exception))
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
            SupervisorUpdateApplyMonitor.BoundError(error)));
    }

    private static bool DesktopAnchorStillRunning(
        IReadOnlyList<CodexDesktopProcessIdentity> expected,
        CodexDesktopObservation current) =>
        current.Kind == CodexDesktopObservationKind.Running &&
        expected.All(current.Processes.Contains);

    private static async Task<IReadOnlyList<string>> ReadThreadIdsAsync(
        int backendPort,
        int backendProcessId,
        CancellationToken cancellationToken)
    {
        await using var client = await Program.RpcClient.ConnectOwnedAsync(
            LoopbackEndpoint.WebSocketUrl(backendPort),
            backendProcessId,
            cancellationToken);
        return await client.ListOwnedThreadIdsAsync(cancellationToken);
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
        catch (Exception exception) when (SupervisorUpdateApplyMonitor.IsExpectedFailure(exception))
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
                SupervisorUpdateApplyMonitor.BoundError(exception.Message)));
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
            !SameOptionalPath(backend.CodexHome, original.CodexHome))
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

    private static bool SameOptionalPath(string? left, string? right) =>
        left is null && right is null ||
        left is not null && right is not null && Path.GetFullPath(left).Equals(
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
}
