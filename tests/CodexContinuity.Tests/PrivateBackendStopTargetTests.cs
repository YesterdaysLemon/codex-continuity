using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class PrivateBackendStopTargetTests
{
    [Fact]
    public async Task AcceptsOnlyThisSupervisorsExactLiveBackendLease()
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync();
        var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
        var lease = backend.CreateLease(publicPort);

        var target = PrivateBackendStopTarget.FromOwnedLease(lease, backend.Process);

        Assert.Equal(backend.Port, target.BackendPort);
        Assert.Equal(backend.Process.Id, target.ProcessId);
        Assert.False(target.HasExited);
        Assert.Throws<InvalidDataException>(() =>
            PrivateBackendStopTarget.FromOwnedLease(
                lease with { OwnerSupervisorProcessId = Environment.ProcessId + 1 },
                backend.Process));
        Assert.Throws<InvalidDataException>(() =>
            PrivateBackendStopTarget.FromOwnedLease(
                lease with { BackendProcessId = backend.Process.Id + 1 },
                backend.Process));
        Assert.Throws<InvalidDataException>(() =>
            PrivateBackendStopTarget.FromOwnedLease(
                lease with { BackendStartedAtUtc = lease.BackendStartedAtUtc.AddSeconds(1) },
                backend.Process));
        Assert.Throws<InvalidDataException>(() =>
            PrivateBackendStopTarget.FromOwnedLease(
                lease with
                {
                    BackendExecutable = Path.Combine(
                        Path.GetDirectoryName(lease.BackendExecutable)!,
                        "foreign.exe"),
                },
                backend.Process));
    }

    [Fact]
    public async Task RejectsLeaseWhosePrivateListenerIsNotOwnedByProcess()
    {
        await using var backend = await PrivateBackendTestProcess.StartAsync();
        var publicPort = PrivateBackendTestProcess.AvailablePort(backend.Port);
        var foreignPort = PrivateBackendTestProcess.AvailablePort(backend.Port, publicPort);
        var lease = backend.CreateLease(publicPort) with { BackendPort = foreignPort };

        Assert.Throws<InvalidDataException>(() =>
            PrivateBackendStopTarget.FromOwnedLease(lease, backend.Process));
        Assert.False(backend.Process.HasExited);
    }
}
