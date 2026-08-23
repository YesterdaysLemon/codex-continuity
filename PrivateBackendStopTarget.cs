using System.ComponentModel;

namespace CodexContinuity;

internal sealed class PrivateBackendStopTarget
{
    private readonly WindowsProcessGroup process;

    private PrivateBackendStopTarget(BackendLease lease, WindowsProcessGroup process)
    {
        BackendPort = lease.BackendPort;
        this.process = process;
    }

    internal int BackendPort { get; }

    internal int ProcessId => process.Id;

    internal bool HasExited => process.HasExited;

    internal static PrivateBackendStopTarget FromOwnedLease(
        BackendLease lease,
        WindowsProcessGroup process)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(process);
        lease.Validate();
        if (lease.OwnerSupervisorProcessId != Environment.ProcessId ||
            process.HasExited ||
            lease.BackendProcessId != process.Id ||
            lease.BackendStartedAtUtc != process.StartedAtUtc ||
            !SamePath(lease.BackendExecutable, process.ExecutablePath))
        {
            throw new InvalidDataException(
                "The private backend target does not match this supervisor's owned lease.");
        }
        try
        {
            if (!WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy(
                    lease.BackendPort,
                    process.Id))
            {
                throw new InvalidDataException(
                    "The owned backend process does not own its leased private listener.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            throw new InvalidDataException(
                "The owned backend listener could not be verified.",
                exception);
        }
        return new PrivateBackendStopTarget(lease, process);
    }

    private static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).Equals(
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
}
