using Xunit;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace CodexContinuity.Tests;

public sealed class BackendLeaseTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-backend-lease-{Guid.NewGuid():N}");

    [Fact]
    public void RoundTripsValidatedLeaseAndDeletesIt()
    {
        var store = Store();
        var lease = ValidLease();

        store.Write(lease);

        Assert.Equal(new BackendLeaseLoadResult(BackendLeaseLoadKind.Loaded, lease), store.Load());
        store.Delete();
        Assert.Equal(
            new BackendLeaseLoadResult(BackendLeaseLoadKind.Missing, Lease: null),
            store.Load());
    }

    [Fact]
    public void InvalidOrOversizedLeaseFailsClosed()
    {
        Directory.CreateDirectory(root);
        var path = ContinuityPaths.BackendLeaseFile(root);
        var store = new BackendLeaseStore(path);

        File.WriteAllText(path, "{not-json");
        Assert.Equal(
            new BackendLeaseLoadResult(BackendLeaseLoadKind.Invalid, Lease: null),
            store.Load());

        File.WriteAllBytes(path, new byte[(64 * 1024) + 1]);
        Assert.Equal(
            new BackendLeaseLoadResult(BackendLeaseLoadKind.Invalid, Lease: null),
            store.Load());
    }

    [Theory]
    [InlineData(0, 45123, 45124)]
    [InlineData(1, 45123, 45123)]
    public void RejectsInvalidOwnershipCoordinates(
        int backendProcessId,
        int publicPort,
        int backendPort)
    {
        var lease = ValidLease() with
        {
            BackendProcessId = backendProcessId,
            PublicPort = publicPort,
            BackendPort = backendPort,
        };

        Assert.ThrowsAny<Exception>(() => Store().Write(lease));
    }

    [Fact]
    public void RecoversOnlyTheExactProcessThatOwnsTheLeasedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var current = Process.GetCurrentProcess();
        var executable = current.MainModule?.FileName
            ?? throw new InvalidOperationException("Could not locate the test process.");
        var lease = ValidLease() with
        {
            OwnerSupervisorProcessId = Environment.ProcessId + 1,
            BackendProcessId = Environment.ProcessId,
            BackendPort = port,
            BackendExecutable = executable,
            CodexHome = root,
            BackendStartedAtUtc = current.StartTime.ToUniversalTime(),
        };
        var store = Store();
        store.Write(lease);

        var result = BackendLeaseRecovery.TryRecover(
            store,
            lease.PublicPort,
            executable,
            root);

        Assert.Equal(BackendRecoveryKind.Recovered, result.Kind);
        Assert.Equal(lease, result.Lease);
        Assert.NotNull(result.Backend);
        result.Backend.Dispose();
    }

    [Fact]
    public void FailsClosedWhenMatchingProcessDoesNotOwnLeasedPort()
    {
        using var current = Process.GetCurrentProcess();
        var executable = current.MainModule?.FileName
            ?? throw new InvalidOperationException("Could not locate the test process.");
        var lease = ValidLease() with
        {
            BackendProcessId = Environment.ProcessId,
            BackendExecutable = executable,
            CodexHome = root,
            BackendStartedAtUtc = current.StartTime.ToUniversalTime(),
        };
        var store = Store();
        store.Write(lease);

        var result = BackendLeaseRecovery.TryRecover(
            store,
            lease.PublicPort,
            executable,
            root);

        Assert.Equal(BackendRecoveryKind.Unsafe, result.Kind);
        Assert.Null(result.Backend);
    }

    private BackendLeaseStore Store()
    {
        Directory.CreateDirectory(root);
        return new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root));
    }

    private static BackendLease ValidLease() => new(
        BackendLease.CurrentSchemaVersion,
        OwnerSupervisorProcessId: 10,
        BackendProcessId: 11,
        PublicPort: 45123,
        BackendPort: 45124,
        BackendExecutable: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "cmd.exe"),
        CodexHome: Path.GetTempPath(),
        BackendStartedAtUtc: DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
