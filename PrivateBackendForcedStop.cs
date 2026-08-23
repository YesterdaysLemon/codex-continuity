using System.ComponentModel;

namespace CodexContinuity;

internal enum PrivateBackendForcedStopKind
{
    NotEligible,
    GateUnavailable,
    BackendOwnershipLost,
    Unknown,
    AlreadyExited,
    ForcedExit,
    TimedOut,
}

internal sealed record PrivateBackendForcedStopChecks(
    Func<int, int, bool> IsListenerOwnedBy,
    Func<PrivateBackendStopTarget, bool> TryForceStop,
    Func<PrivateBackendStopTarget, TimeSpan, Task<bool>> WaitForExit)
{
    internal static PrivateBackendForcedStopChecks Native { get; } = new(
        WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy,
        static target => target.TryForceStop(),
        static (target, timeout) => target.WaitForExitWithinAsync(timeout));

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(IsListenerOwnedBy);
        ArgumentNullException.ThrowIfNull(TryForceStop);
        ArgumentNullException.ThrowIfNull(WaitForExit);
    }
}

internal sealed class PrivateBackendForcedStopResult
{
    private readonly RelayBackendStopReservation? pendingStopReservation;

    private PrivateBackendForcedStopResult(
        PrivateBackendForcedStopKind kind,
        RelayBackendStopReservation? pendingStopReservation)
    {
        Kind = kind;
        this.pendingStopReservation = pendingStopReservation;
    }

    internal PrivateBackendForcedStopKind Kind { get; }

    internal bool HasPendingStopReservation => pendingStopReservation?.IsCurrent == true;

    internal static PrivateBackendForcedStopResult Settled(
        PrivateBackendForcedStopKind kind) => new(kind, pendingStopReservation: null);

    internal static PrivateBackendForcedStopResult Pending(
        PrivateBackendForcedStopKind kind,
        RelayBackendStopReservation pendingStopReservation) => new(
            kind,
            pendingStopReservation);
}

internal static class PrivateBackendForcedStop
{
    private static readonly TimeSpan MaximumWaitTimeout = TimeSpan.FromSeconds(30);

    internal static async Task<PrivateBackendForcedStopResult> StopAsync(
        PrivateBackendGracefulStopResult gracefulResult,
        PrivateBackendStopTarget target,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken,
        PrivateBackendForcedStopChecks? checks = null)
    {
        ArgumentNullException.ThrowIfNull(gracefulResult);
        ArgumentNullException.ThrowIfNull(target);
        if (waitTimeout <= TimeSpan.Zero || waitTimeout > MaximumWaitTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        }
        checks ??= PrivateBackendForcedStopChecks.Native;
        checks.Validate();
        if (gracefulResult.Kind != PrivateBackendGracefulStopKind.TimedOut)
        {
            return Settled(PrivateBackendForcedStopKind.NotEligible);
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool alreadyExited;
        try
        {
            alreadyExited = target.HasExited;
            if (!alreadyExited &&
                !checks.IsListenerOwnedBy(target.BackendPort, target.ProcessId))
            {
                return Settled(PrivateBackendForcedStopKind.BackendOwnershipLost);
            }
        }
        catch (Exception exception) when (
            exception is Win32Exception or IOException or InvalidDataException)
        {
            return Settled(PrivateBackendForcedStopKind.Unknown);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!gracefulResult.HasPendingStopReservation)
        {
            return Settled(PrivateBackendForcedStopKind.GateUnavailable);
        }
        var reservation = gracefulResult.TryTakeTimedOutReservation();
        if (reservation is null)
        {
            return Settled(PrivateBackendForcedStopKind.GateUnavailable);
        }

        var keepReservation = true;
        try
        {
            if (!reservation.IsCurrent || reservation.BackendPort != target.BackendPort)
            {
                return Pending(PrivateBackendForcedStopKind.GateUnavailable, reservation);
            }
            if (alreadyExited)
            {
                keepReservation = false;
                return Settled(PrivateBackendForcedStopKind.AlreadyExited);
            }

            bool forceStarted;
            try
            {
                forceStarted = checks.TryForceStop(target);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception)
            {
                return Pending(PrivateBackendForcedStopKind.Unknown, reservation);
            }
            if (!forceStarted)
            {
                keepReservation = false;
                return Settled(PrivateBackendForcedStopKind.AlreadyExited);
            }

            try
            {
                if (!await checks.WaitForExit(target, waitTimeout))
                {
                    return Pending(PrivateBackendForcedStopKind.TimedOut, reservation);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception)
            {
                return Pending(PrivateBackendForcedStopKind.Unknown, reservation);
            }

            keepReservation = false;
            return Settled(PrivateBackendForcedStopKind.ForcedExit);
        }
        finally
        {
            if (!keepReservation)
            {
                reservation.Dispose();
            }
        }
    }

    private static PrivateBackendForcedStopResult Settled(
        PrivateBackendForcedStopKind kind) => PrivateBackendForcedStopResult.Settled(kind);

    private static PrivateBackendForcedStopResult Pending(
        PrivateBackendForcedStopKind kind,
        RelayBackendStopReservation reservation) =>
        PrivateBackendForcedStopResult.Pending(kind, reservation);
}
