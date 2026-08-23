using System.ComponentModel;

namespace CodexContinuity;

internal enum PrivateBackendGracefulStopKind
{
    BlockedByPlan,
    GateUnavailable,
    BackendIdentityMismatch,
    BackendOwnershipLost,
    Unknown,
    AlreadyExited,
    CleanExit,
    WindowsControlExit,
    UnexpectedExit,
    TimedOut,
    CallerCanceled,
}

internal sealed record PrivateBackendGracefulStopChecks(
    Func<int, int, bool> IsListenerOwnedBy,
    Func<PrivateBackendStopTarget, TimeSpan, CancellationToken,
        Task<Program.AppServerStopDisposition>> StopTarget)
{
    internal static PrivateBackendGracefulStopChecks Native { get; } = new(
        WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy,
        static (target, timeout, cancellationToken) =>
            target.StopGracefullyAsync(timeout, cancellationToken));

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(IsListenerOwnedBy);
        ArgumentNullException.ThrowIfNull(StopTarget);
    }
}

internal sealed class PrivateBackendGracefulStopResult
{
    private readonly RelayBackendStopReservation? pendingStopReservation;

    private PrivateBackendGracefulStopResult(
        PrivateBackendGracefulStopKind kind,
        RelayBackendStopReservation? pendingStopReservation)
    {
        Kind = kind;
        this.pendingStopReservation = pendingStopReservation;
    }

    internal PrivateBackendGracefulStopKind Kind { get; }

    internal bool HasPendingStopReservation => pendingStopReservation?.IsCurrent == true;

    internal static PrivateBackendGracefulStopResult Settled(
        PrivateBackendGracefulStopKind kind) => new(kind, pendingStopReservation: null);

    internal static PrivateBackendGracefulStopResult Pending(
        PrivateBackendGracefulStopKind kind,
        RelayBackendStopReservation pendingStopReservation) => new(
            kind,
            pendingStopReservation);
}

internal static class PrivateBackendGracefulStop
{
    private static readonly TimeSpan MaximumStopTimeout = TimeSpan.FromSeconds(30);

    internal static async Task<PrivateBackendGracefulStopResult> StopAsync(
        GatedHandoffDecision decision,
        PrivateBackendStopTarget target,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        PrivateBackendGracefulStopChecks? checks = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(target);
        if (timeout <= TimeSpan.Zero || timeout > MaximumStopTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        checks ??= PrivateBackendGracefulStopChecks.Native;
        checks.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        if (!decision.Plan.TransitionReady || !decision.Plan.BackendReady)
        {
            return PrivateBackendGracefulStopResult.Settled(
                PrivateBackendGracefulStopKind.BlockedByPlan);
        }
        if (decision.GateLease is null)
        {
            return PrivateBackendGracefulStopResult.Settled(
                PrivateBackendGracefulStopKind.GateUnavailable);
        }

        var reservation = decision.GateLease.TryReserveBackendStop();
        if (reservation is null)
        {
            return PrivateBackendGracefulStopResult.Settled(
                PrivateBackendGracefulStopKind.GateUnavailable);
        }
        var keepReservation = false;
        try
        {
            if (reservation.BackendPort != target.BackendPort)
            {
                return PrivateBackendGracefulStopResult.Settled(
                    PrivateBackendGracefulStopKind.BackendIdentityMismatch);
            }

            try
            {
                if (target.HasExited)
                {
                    return PrivateBackendGracefulStopResult.Settled(
                        PrivateBackendGracefulStopKind.AlreadyExited);
                }
                if (!checks.IsListenerOwnedBy(target.BackendPort, target.ProcessId))
                {
                    return PrivateBackendGracefulStopResult.Settled(
                        PrivateBackendGracefulStopKind.BackendOwnershipLost);
                }
            }
            catch (Exception exception) when (
                exception is Win32Exception or IOException or InvalidDataException)
            {
                return PrivateBackendGracefulStopResult.Settled(
                    PrivateBackendGracefulStopKind.Unknown);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!reservation.IsCurrent)
            {
                return PrivateBackendGracefulStopResult.Settled(
                    PrivateBackendGracefulStopKind.GateUnavailable);
            }

            try
            {
                var disposition = await checks.StopTarget(
                    target,
                    timeout,
                    cancellationToken);
                if (disposition == Program.AppServerStopDisposition.TimedOut)
                {
                    keepReservation = true;
                    return PrivateBackendGracefulStopResult.Pending(
                        PrivateBackendGracefulStopKind.TimedOut,
                        reservation);
                }
                return PrivateBackendGracefulStopResult.Settled(disposition switch
                {
                    Program.AppServerStopDisposition.AlreadyExited =>
                        PrivateBackendGracefulStopKind.AlreadyExited,
                    Program.AppServerStopDisposition.CleanExit =>
                        PrivateBackendGracefulStopKind.CleanExit,
                    Program.AppServerStopDisposition.WindowsControlExit =>
                        PrivateBackendGracefulStopKind.WindowsControlExit,
                    Program.AppServerStopDisposition.UnexpectedExit =>
                        PrivateBackendGracefulStopKind.UnexpectedExit,
                    _ => throw new InvalidOperationException(
                        "Unknown private backend stop outcome."),
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                keepReservation = true;
                return PrivateBackendGracefulStopResult.Pending(
                    PrivateBackendGracefulStopKind.CallerCanceled,
                    reservation);
            }
            catch (Win32Exception)
            {
                keepReservation = true;
                return PrivateBackendGracefulStopResult.Pending(
                    PrivateBackendGracefulStopKind.Unknown,
                    reservation);
            }
        }
        finally
        {
            if (!keepReservation)
            {
                reservation.Dispose();
            }
        }
    }
}
