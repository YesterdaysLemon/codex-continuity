namespace CodexContinuity;

internal enum PrivateBackendTransitionKind
{
    BlockedByPlan,
    GracefulExit,
    ForcedExit,
    Unsafe,
}

internal sealed record PrivateBackendTransitionChecks(
    Func<string, int, int, CancellationToken, Task<ContinuityHandoffPlan>> Observe,
    PrivateBackendGracefulStopChecks GracefulStop,
    PrivateBackendForcedStopChecks ForcedStop)
{
    internal static PrivateBackendTransitionChecks Native { get; } = new(
        static (stateDirectory, backendPort, backendProcessId, cancellationToken) =>
            PrivateBackendHandoffObserver.ObserveAsync(
                stateDirectory,
                backendPort,
                backendProcessId,
                cancellationToken),
        PrivateBackendGracefulStopChecks.Native,
        PrivateBackendForcedStopChecks.Native);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Observe);
        ArgumentNullException.ThrowIfNull(GracefulStop);
        ArgumentNullException.ThrowIfNull(ForcedStop);
        GracefulStop.Validate();
        ForcedStop.Validate();
    }
}

internal sealed record PrivateBackendTransitionResult(
    PrivateBackendTransitionKind Kind,
    ContinuityHandoffPlan Plan,
    RelayGateLease? ReplacementGateLease,
    PrivateBackendGracefulStopResult? GracefulStop,
    PrivateBackendForcedStopResult? ForcedStop)
{
    internal bool CanStartReplacement => ReplacementGateLease is not null;
}

internal static class PrivateBackendTransition
{
    private static readonly TimeSpan MaximumStopTimeout = TimeSpan.FromSeconds(30);

    internal static async Task<PrivateBackendTransitionResult> StopForReplacementAsync(
        LoopbackRelay relay,
        string stateDirectory,
        BackendLease lease,
        WindowsProcessGroup process,
        TimeSpan gracefulTimeout,
        TimeSpan forcedWaitTimeout,
        CancellationToken cancellationToken,
        PrivateBackendTransitionChecks? checks = null)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(process);
        ValidateStopTimeout(gracefulTimeout, nameof(gracefulTimeout));
        ValidateStopTimeout(forcedWaitTimeout, nameof(forcedWaitTimeout));
        lease.Validate();
        if (lease.PublicPort != relay.PublicPort)
        {
            throw new InvalidDataException(
                "The private backend lease does not match the stable relay endpoint.");
        }
        checks ??= PrivateBackendTransitionChecks.Native;
        checks.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var target = PrivateBackendStopTarget.FromOwnedLease(lease, process);
        var decision = await GatedHandoffTransition.CloseAndRecomputeAsync(
            relay,
            async transitionToken =>
            {
                using var observation = CancellationTokenSource.CreateLinkedTokenSource(
                    transitionToken,
                    cancellationToken);
                return await checks.Observe(
                    stateDirectory,
                    lease.BackendPort,
                    target.ProcessId,
                    observation.Token);
            });
        if (!decision.Plan.TransitionReady)
        {
            return new(
                PrivateBackendTransitionKind.BlockedByPlan,
                decision.Plan,
                ReplacementGateLease: null,
                GracefulStop: null,
                ForcedStop: null);
        }

        PrivateBackendGracefulStopResult graceful;
        try
        {
            graceful = await PrivateBackendGracefulStop.StopAsync(
                decision,
                target,
                gracefulTimeout,
                cancellationToken,
                checks.GracefulStop);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            if (decision.GateLease?.TryOpen() != true)
            {
                throw new InvalidOperationException(
                    "Cancellation could not reopen the stable relay; it remains gated.",
                    exception);
            }
            throw;
        }

        if (IsGracefulExit(graceful.Kind))
        {
            return new(
                PrivateBackendTransitionKind.GracefulExit,
                decision.Plan,
                decision.GateLease,
                graceful,
                ForcedStop: null);
        }
        if (graceful.Kind != PrivateBackendGracefulStopKind.TimedOut)
        {
            return Unsafe(decision.Plan, graceful, forced: null);
        }

        var forced = await PrivateBackendForcedStop.StopAsync(
            graceful,
            target,
            forcedWaitTimeout,
            CancellationToken.None,
            checks.ForcedStop);
        return IsForcedExit(forced.Kind)
            ? new(
                PrivateBackendTransitionKind.ForcedExit,
                decision.Plan,
                decision.GateLease,
                graceful,
                forced)
            : Unsafe(decision.Plan, graceful, forced);
    }

    private static bool IsGracefulExit(PrivateBackendGracefulStopKind kind) => kind is
        PrivateBackendGracefulStopKind.AlreadyExited or
        PrivateBackendGracefulStopKind.CleanExit or
        PrivateBackendGracefulStopKind.WindowsControlExit or
        PrivateBackendGracefulStopKind.UnexpectedExit;

    private static bool IsForcedExit(PrivateBackendForcedStopKind kind) => kind is
        PrivateBackendForcedStopKind.AlreadyExited or
        PrivateBackendForcedStopKind.ForcedExit;

    private static void ValidateStopTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout > MaximumStopTimeout)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static PrivateBackendTransitionResult Unsafe(
        ContinuityHandoffPlan plan,
        PrivateBackendGracefulStopResult graceful,
        PrivateBackendForcedStopResult? forced) => new(
            PrivateBackendTransitionKind.Unsafe,
            plan,
            ReplacementGateLease: null,
            graceful,
            forced);
}
