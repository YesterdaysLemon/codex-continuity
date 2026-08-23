using System.ComponentModel;

namespace CodexContinuity;

internal enum PreviousSupervisorObservationKind
{
    Observed,
    Missing,
    Unsafe,
}

internal sealed class PreviousSupervisorObservation : IDisposable
{
    private readonly Func<bool> hasExited;
    private readonly Func<CancellationToken, Task> waitForExit;
    private readonly Action dispose;
    private int disposed;

    internal PreviousSupervisorObservation(
        int processId,
        DateTimeOffset startedAtUtc,
        string executable,
        Func<bool> hasExited,
        Func<CancellationToken, Task> waitForExit,
        Action? dispose = null)
    {
        if (processId <= 0 || startedAtUtc == default)
        {
            throw new ArgumentException("Observed supervisor identity is invalid.");
        }
        if (!Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException("Observed supervisor executable must be fully qualified.");
        }
        ArgumentNullException.ThrowIfNull(hasExited);
        ArgumentNullException.ThrowIfNull(waitForExit);
        ProcessId = processId;
        StartedAtUtc = startedAtUtc;
        Executable = executable;
        this.hasExited = hasExited;
        this.waitForExit = waitForExit;
        this.dispose = dispose ?? (() => { });
    }

    internal int ProcessId { get; }
    internal DateTimeOffset StartedAtUtc { get; }
    internal string Executable { get; }
    internal bool HasExited => hasExited();
    internal Task WaitForExitAsync(CancellationToken cancellationToken) =>
        waitForExit(cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            dispose();
        }
    }
}

internal sealed record PreviousSupervisorObservationResult(
    PreviousSupervisorObservationKind Kind,
    PreviousSupervisorObservation? Observation);

internal sealed record SupervisorSuccessorAdmissionChecks(
    Func<DateTimeOffset> UtcNow,
    Func<int, PreviousSupervisorObservationResult> ObservePreviousSupervisor,
    Func<string, SupervisorExecutableIdentity?> ResolveExecutable)
{
    internal static SupervisorSuccessorAdmissionChecks Native { get; } = new(
        () => DateTimeOffset.UtcNow,
        ObserveNative,
        ResolveNativeExecutable);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(UtcNow);
        ArgumentNullException.ThrowIfNull(ObservePreviousSupervisor);
        ArgumentNullException.ThrowIfNull(ResolveExecutable);
    }

    private static PreviousSupervisorObservationResult ObserveNative(int processId)
    {
        WindowsProcessGroup? process = null;
        try
        {
            process = WindowsProcessGroup.Attach(processId);
            var ownedProcess = process;
            var observation = new PreviousSupervisorObservation(
                ownedProcess.Id,
                ownedProcess.StartedAtUtc,
                ownedProcess.ExecutablePath,
                () => ownedProcess.HasExited,
                ownedProcess.WaitForExitAsync,
                ownedProcess.Dispose);
            process = null;
            return new(PreviousSupervisorObservationKind.Observed, observation);
        }
        catch (ArgumentException)
        {
            return new(PreviousSupervisorObservationKind.Missing, Observation: null);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return new(PreviousSupervisorObservationKind.Unsafe, Observation: null);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static SupervisorExecutableIdentity? ResolveNativeExecutable(string executable)
    {
        var build = AutomaticUpdateRunner.ResolveBuildIdentity(executable);
        return build is null
            ? null
            : new(build.Version, Path.GetFullPath(executable), build.ExecutableSha256);
    }
}

internal enum SupervisorSuccessorAdmissionKind
{
    Admitted,
    HandoffUnavailable,
    HandoffMismatch,
    EndpointMismatch,
    SuccessorMismatch,
    BackendLeaseMismatch,
    PreviousSupervisorMissing,
    PreviousSupervisorUnsafe,
    PreviousSupervisorMismatch,
}

internal enum PreviousSupervisorWaitKind
{
    Exited,
    Expired,
    Unsafe,
}

internal sealed record SupervisorSuccessorAdmissionResult(
    SupervisorSuccessorAdmissionKind Kind,
    SupervisorSuccessorAdmission? Admission);

internal sealed class SupervisorSuccessorAdmission : IDisposable
{
    private readonly Func<DateTimeOffset> utcNow;
    private readonly PreviousSupervisorObservation previousSupervisor;
    private int disposed;

    private SupervisorSuccessorAdmission(
        SupervisorSuccessorHandoff handoff,
        PreviousSupervisorObservation previousSupervisor,
        Func<DateTimeOffset> utcNow)
    {
        Handoff = handoff;
        this.previousSupervisor = previousSupervisor;
        this.utcNow = utcNow;
    }

    internal SupervisorSuccessorHandoff Handoff { get; }

    internal static SupervisorSuccessorAdmissionResult TryCreate(
        string stateDirectory,
        string handoffId,
        int publicPort,
        string? codexHome,
        string successorExecutable,
        SupervisorSuccessorAdmissionChecks? checks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(handoffId);
        ArgumentException.ThrowIfNullOrWhiteSpace(successorExecutable);
        LoopbackEndpoint.ValidatePort(publicPort);
        checks ??= SupervisorSuccessorAdmissionChecks.Native;
        checks.Validate();

        var handoffLoad = new SupervisorSuccessorHandoffStore(
            ContinuityPaths.SupervisorHandoffFile(stateDirectory)).Load(checks.UtcNow());
        if (handoffLoad.Kind != SupervisorSuccessorHandoffLoadKind.Loaded ||
            handoffLoad.Handoff is not { } handoff)
        {
            return Failure(SupervisorSuccessorAdmissionKind.HandoffUnavailable);
        }
        if (!handoff.HandoffId.Equals(handoffId, StringComparison.Ordinal))
        {
            return Failure(SupervisorSuccessorAdmissionKind.HandoffMismatch);
        }
        if (handoff.PublicPort != publicPort ||
            !SameOptionalPath(handoff.CodexHome, codexHome))
        {
            return Failure(SupervisorSuccessorAdmissionKind.EndpointMismatch);
        }
        var successor = checks.ResolveExecutable(successorExecutable);
        if (successor is null || !SameExecutable(handoff.SelectedBuild, successor))
        {
            return Failure(SupervisorSuccessorAdmissionKind.SuccessorMismatch);
        }

        var leaseLoad = new BackendLeaseStore(
            ContinuityPaths.BackendLeaseFile(stateDirectory)).Load();
        if (leaseLoad.Kind != BackendLeaseLoadKind.Loaded ||
            leaseLoad.Lease != handoff.Backend)
        {
            return Failure(SupervisorSuccessorAdmissionKind.BackendLeaseMismatch);
        }

        var previous = checks.ObservePreviousSupervisor(handoff.PreviousSupervisorProcessId);
        if (previous.Kind == PreviousSupervisorObservationKind.Missing)
        {
            previous.Observation?.Dispose();
            return Failure(SupervisorSuccessorAdmissionKind.PreviousSupervisorMissing);
        }
        if (previous.Kind != PreviousSupervisorObservationKind.Observed ||
            previous.Observation is not { } observation)
        {
            previous.Observation?.Dispose();
            return Failure(SupervisorSuccessorAdmissionKind.PreviousSupervisorUnsafe);
        }

        var admitted = false;
        try
        {
            var runningBuild = checks.ResolveExecutable(observation.Executable);
            if (observation.ProcessId != handoff.PreviousSupervisorProcessId ||
                observation.StartedAtUtc != handoff.PreviousSupervisorStartedAtUtc ||
                runningBuild is null ||
                !SameExecutable(handoff.RunningBuild, runningBuild))
            {
                return Failure(SupervisorSuccessorAdmissionKind.PreviousSupervisorMismatch);
            }

            admitted = true;
            return new(
                SupervisorSuccessorAdmissionKind.Admitted,
                new SupervisorSuccessorAdmission(handoff, observation, checks.UtcNow));
        }
        finally
        {
            if (!admitted)
            {
                observation.Dispose();
            }
        }
    }

    internal async Task<PreviousSupervisorWaitKind> WaitForPreviousExitAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        try
        {
            if (previousSupervisor.HasExited)
            {
                return PreviousSupervisorWaitKind.Exited;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return PreviousSupervisorWaitKind.Unsafe;
        }

        var remaining = Handoff.ExpiresAtUtc - utcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return PreviousSupervisorWaitKind.Expired;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(remaining);
        try
        {
            await previousSupervisor.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return PreviousSupervisorWaitKind.Expired;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return PreviousSupervisorWaitKind.Unsafe;
        }
        try
        {
            return previousSupervisor.HasExited
                ? PreviousSupervisorWaitKind.Exited
                : PreviousSupervisorWaitKind.Unsafe;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return PreviousSupervisorWaitKind.Unsafe;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            previousSupervisor.Dispose();
        }
    }

    private static SupervisorSuccessorAdmissionResult Failure(
        SupervisorSuccessorAdmissionKind kind) => new(kind, Admission: null);

    private static bool SameExecutable(
        SupervisorExecutableIdentity left,
        SupervisorExecutableIdentity right) =>
        left.Version.Equals(right.Version, StringComparison.OrdinalIgnoreCase) &&
        left.ExecutableSha256.Equals(right.ExecutableSha256, StringComparison.OrdinalIgnoreCase) &&
        SamePath(left.Executable, right.Executable);

    private static bool SameOptionalPath(string? left, string? right) =>
        left is null && right is null ||
        left is not null && right is not null && SamePath(left, right);

    private static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).Equals(
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
}
