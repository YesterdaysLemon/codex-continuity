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

    [Fact]
    public void DirectoryAtLeasePathIsInvalidRatherThanMissing()
    {
        var path = ContinuityPaths.BackendLeaseFile(root);
        Directory.CreateDirectory(path);

        Assert.Equal(
            new BackendLeaseLoadResult(BackendLeaseLoadKind.Invalid, Lease: null),
            new BackendLeaseStore(path).Load());
    }

    [Fact]
    public void RefusesToWriteLeaseThatCannotBeReadWithinBound()
    {
        var lease = ValidLease() with
        {
            BackendExecutable = $"C:\\{new string('a', 70 * 1024)}",
        };

        Assert.Throws<InvalidDataException>(() => Store().Write(lease));
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
            OwnerSupervisorProcessId = int.MaxValue,
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
            OwnerSupervisorProcessId = int.MaxValue,
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

        Assert.Equal(
            new BackendRecoveryResult(
                BackendRecoveryKind.Unsafe,
                Backend: null,
                lease,
                "The leased backend does not own its private loopback port."),
            result);
    }

    [Fact]
    public void DistinguishesMissingStaleAndInstallationMismatch()
    {
        var store = Store();
        Assert.Equal(
            new BackendRecoveryResult(
                BackendRecoveryKind.None,
                Backend: null,
                Lease: null,
                Detail: null),
            BackendLeaseRecovery.TryRecover(
                store,
                45123,
                ValidLease().BackendExecutable,
                ValidLease().CodexHome));

        var staleLease = ValidLease() with
        {
            OwnerSupervisorProcessId = int.MaxValue - 1,
            BackendProcessId = int.MaxValue,
        };
        store.Write(staleLease);
        Assert.Equal(
            new BackendRecoveryResult(
                BackendRecoveryKind.Stale,
                Backend: null,
                staleLease,
                "The leased backend process no longer exists."),
            BackendLeaseRecovery.TryRecover(
                store,
                staleLease.PublicPort,
                staleLease.BackendExecutable,
                staleLease.CodexHome));

        var mismatch = BackendLeaseRecovery.TryRecover(
            store,
            45125,
            staleLease.BackendExecutable,
            staleLease.CodexHome);
        Assert.Equal(
            new BackendRecoveryResult(
                BackendRecoveryKind.Unsafe,
                Backend: null,
                staleLease,
                "The persisted backend lease does not match this installation."),
            mismatch);
    }

    [Fact]
    public void MalformedLeaseProducesUnsafeRecoveryDecision()
    {
        var store = Store();
        File.WriteAllText(ContinuityPaths.BackendLeaseFile(root), "{not-json");

        Assert.Equal(
            new BackendRecoveryResult(
                BackendRecoveryKind.Unsafe,
                Backend: null,
                Lease: null,
                "The persisted backend lease is invalid."),
            BackendLeaseRecovery.TryRecover(
                store,
                45123,
                ValidLease().BackendExecutable,
                ValidLease().CodexHome));
    }

    [Fact]
    public void RejectsRecoveryWhileRecordedSupervisorIsAlive()
    {
        var lease = CurrentProcessLease(backendPort: 45124) with
        {
            OwnerSupervisorProcessId = Environment.ProcessId,
        };
        var store = Store();
        store.Write(lease);

        Assert.Equal(
            new BackendRecoveryResult(
                BackendRecoveryKind.Unsafe,
                Backend: null,
                lease,
                "The supervisor recorded in the backend lease is still running or unreadable."),
            BackendLeaseRecovery.TryRecover(
                store,
                lease.PublicPort,
                lease.BackendExecutable,
                lease.CodexHome));
    }

    [Fact]
    public void PortInspectionFailureProducesUnsafeRecoveryDecision()
    {
        var lease = CurrentProcessLease(backendPort: 45124);
        var store = Store();
        store.Write(lease);

        var result = BackendLeaseRecovery.TryRecover(
            store,
            lease.PublicPort,
            lease.BackendExecutable,
            lease.CodexHome,
            (_, _) => throw new IOException("Injected TCP table failure."));

        Assert.Equal(
            new BackendRecoveryResult(
                BackendRecoveryKind.Unsafe,
                Backend: null,
                lease,
                "Private loopback port ownership could not be inspected."),
            result);
    }

    private BackendLeaseStore Store()
    {
        Directory.CreateDirectory(root);
        return new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root));
    }

    private static BackendLease ValidLease() => new(
        BackendLease.CurrentSchemaVersion,
        OwnerSupervisorProcessId: int.MaxValue,
        BackendProcessId: 11,
        PublicPort: 45123,
        BackendPort: 45124,
        BackendExecutable: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "cmd.exe"),
        CodexHome: Path.GetTempPath(),
        BackendStartedAtUtc: DateTimeOffset.UtcNow);

    private BackendLease CurrentProcessLease(int backendPort)
    {
        using var current = Process.GetCurrentProcess();
        return ValidLease() with
        {
            BackendProcessId = Environment.ProcessId,
            BackendPort = backendPort,
            BackendExecutable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not locate the test process."),
            CodexHome = root,
            BackendStartedAtUtc = current.StartTime.ToUniversalTime(),
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
