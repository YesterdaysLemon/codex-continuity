using System.ComponentModel;

namespace CodexContinuity;

internal enum PrivateBackendStopKind
{
    BlockedByPlan,
    GateOwnershipLost,
    BackendOwnershipLost,
    BackendOwnershipUnknown,
    AlreadyExited,
    GracefulExit,
    WindowsControlExit,
    UnexpectedExit,
    ForcedExit,
    ForceTimedOut,
}

internal sealed record PrivateBackendStopTarget(
    int Port,
    int ProcessId,
    Func<bool> HasExited,
    Func<TimeSpan, CancellationToken, Task<Program.AppServerStopDisposition>> StopGracefully,
    Action ForceStop,
    Func<CancellationToken, Task> WaitForExit)
{
    internal static PrivateBackendStopTarget From(
        int port,
        WindowsProcessGroup process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return new(
            port,
            process.Id,
            () => process.HasExited,
            (timeout, cancellationToken) => Program.StopAppServerWithCtrlBreakAsync(
                process,
                timeout,
                cancellationToken),
            process.Kill,
            process.WaitForExitAsync);
    }

    internal void Validate()
    {
        LoopbackEndpoint.ValidatePort(Port);
        if (ProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ProcessId));
        }
        ArgumentNullException.ThrowIfNull(HasExited);
        ArgumentNullException.ThrowIfNull(StopGracefully);
        ArgumentNullException.ThrowIfNull(ForceStop);
        ArgumentNullException.ThrowIfNull(WaitForExit);
    }
}

internal static class SafePrivateBackendStop
{
    private enum PrivateBackendOwnership
    {
        Owned,
        Lost,
        Unknown,
    }

    internal static async Task<PrivateBackendStopKind> StopAsync(
        GatedHandoffDecision decision,
        PrivateBackendStopTarget target,
        TimeSpan gracefulTimeout,
        TimeSpan forceTimeout,
        CancellationToken cancellationToken,
        Func<int, int, bool>? isListenerOwnedBy = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        ValidateTimeout(gracefulTimeout, nameof(gracefulTimeout));
        ValidateTimeout(forceTimeout, nameof(forceTimeout));
        isListenerOwnedBy ??= WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy;

        var gateLease = decision.GateLease;
        if (!decision.Plan.TransitionReady || gateLease is null)
        {
            return PrivateBackendStopKind.BlockedByPlan;
        }
        if (!gateLease.IsCurrent)
        {
            return PrivateBackendStopKind.GateOwnershipLost;
        }
        if (target.HasExited())
        {
            return PrivateBackendStopKind.AlreadyExited;
        }

        var ownership = InspectOwnership(target, isListenerOwnedBy);
        if (ownership != PrivateBackendOwnership.Owned)
        {
            return OwnershipFailure(ownership);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!gateLease.IsCurrent)
        {
            return PrivateBackendStopKind.GateOwnershipLost;
        }

        var graceful = await target.StopGracefully(gracefulTimeout, cancellationToken);
        if (graceful != Program.AppServerStopDisposition.TimedOut)
        {
            return graceful switch
            {
                Program.AppServerStopDisposition.CleanExit =>
                    PrivateBackendStopKind.GracefulExit,
                Program.AppServerStopDisposition.WindowsControlExit =>
                    PrivateBackendStopKind.WindowsControlExit,
                Program.AppServerStopDisposition.AlreadyExited =>
                    PrivateBackendStopKind.AlreadyExited,
                Program.AppServerStopDisposition.UnexpectedExit =>
                    PrivateBackendStopKind.UnexpectedExit,
                Program.AppServerStopDisposition.TimedOut =>
                    throw new InvalidOperationException("Timed-out stop was not handled."),
                _ => throw new InvalidOperationException("Unknown app-server stop outcome."),
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!gateLease.IsCurrent)
        {
            return PrivateBackendStopKind.GateOwnershipLost;
        }
        if (target.HasExited())
        {
            return PrivateBackendStopKind.AlreadyExited;
        }
        ownership = InspectOwnership(target, isListenerOwnedBy);
        if (ownership != PrivateBackendOwnership.Owned)
        {
            return OwnershipFailure(ownership);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!gateLease.IsCurrent)
        {
            return PrivateBackendStopKind.GateOwnershipLost;
        }

        target.ForceStop();
        using var forceDeadline = new CancellationTokenSource(forceTimeout);
        try
        {
            await target.WaitForExit(forceDeadline.Token);
            return PrivateBackendStopKind.ForcedExit;
        }
        catch (OperationCanceledException) when (forceDeadline.IsCancellationRequested)
        {
            return PrivateBackendStopKind.ForceTimedOut;
        }
    }

    private static PrivateBackendOwnership InspectOwnership(
        PrivateBackendStopTarget target,
        Func<int, int, bool> isListenerOwnedBy)
    {
        try
        {
            return isListenerOwnedBy(target.Port, target.ProcessId)
                ? PrivateBackendOwnership.Owned
                : PrivateBackendOwnership.Lost;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            return PrivateBackendOwnership.Unknown;
        }
    }

    private static PrivateBackendStopKind OwnershipFailure(PrivateBackendOwnership ownership) =>
        ownership switch
        {
            PrivateBackendOwnership.Lost => PrivateBackendStopKind.BackendOwnershipLost,
            PrivateBackendOwnership.Unknown => PrivateBackendStopKind.BackendOwnershipUnknown,
            PrivateBackendOwnership.Owned =>
                throw new InvalidOperationException("Owned backend is not an ownership failure."),
            _ => throw new InvalidOperationException("Unknown backend ownership outcome."),
        };

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
