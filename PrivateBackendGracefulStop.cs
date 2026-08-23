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
}

internal sealed record PrivateBackendGracefulStopChecks(
    Func<int, int, bool> IsListenerOwnedBy)
{
    internal static PrivateBackendGracefulStopChecks Native { get; } = new(
        WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy);

    internal void Validate() => ArgumentNullException.ThrowIfNull(IsListenerOwnedBy);
}

internal static class PrivateBackendGracefulStop
{
    private static readonly TimeSpan MaximumStopTimeout = TimeSpan.FromSeconds(30);

    internal static async Task<PrivateBackendGracefulStopKind> StopAsync(
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
            return PrivateBackendGracefulStopKind.BlockedByPlan;
        }
        if (decision.GateLease is null)
        {
            return PrivateBackendGracefulStopKind.GateUnavailable;
        }

        using var reservation = decision.GateLease.TryReserveBackendStop();
        if (reservation is null)
        {
            return PrivateBackendGracefulStopKind.GateUnavailable;
        }
        if (reservation.BackendPort != target.BackendPort)
        {
            return PrivateBackendGracefulStopKind.BackendIdentityMismatch;
        }

        try
        {
            if (target.HasExited)
            {
                return PrivateBackendGracefulStopKind.AlreadyExited;
            }
            if (!checks.IsListenerOwnedBy(target.BackendPort, target.ProcessId))
            {
                return PrivateBackendGracefulStopKind.BackendOwnershipLost;
            }
        }
        catch (Exception exception) when (
            exception is Win32Exception or IOException or InvalidDataException)
        {
            return PrivateBackendGracefulStopKind.Unknown;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!reservation.IsCurrent)
        {
            return PrivateBackendGracefulStopKind.GateUnavailable;
        }

        try
        {
            var disposition = await target.StopGracefullyAsync(timeout, cancellationToken);
            return disposition switch
            {
                Program.AppServerStopDisposition.AlreadyExited =>
                    PrivateBackendGracefulStopKind.AlreadyExited,
                Program.AppServerStopDisposition.CleanExit =>
                    PrivateBackendGracefulStopKind.CleanExit,
                Program.AppServerStopDisposition.WindowsControlExit =>
                    PrivateBackendGracefulStopKind.WindowsControlExit,
                Program.AppServerStopDisposition.UnexpectedExit =>
                    PrivateBackendGracefulStopKind.UnexpectedExit,
                Program.AppServerStopDisposition.TimedOut =>
                    PrivateBackendGracefulStopKind.TimedOut,
                _ => throw new InvalidOperationException("Unknown private backend stop outcome."),
            };
        }
        catch (Win32Exception)
        {
            return PrivateBackendGracefulStopKind.Unknown;
        }
    }
}
