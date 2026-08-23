using CodexContinuity;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class InstallPortSafetyTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-install-safety-{Guid.NewGuid():N}");

    [Fact]
    public async Task ManagedUninstallDoesNotPreserveAHealthyForeignEndpoint()
    {
        var legacyProbeCount = 0;

        var policy = await Program.ResolveUninstallReconnectPolicyAsync(
            managedInstalledPort: 45123,
            legacyInstalledPort: null,
            configuredUrl: LoopbackEndpoint.WebSocketUrl(45123),
            _ => Task.FromResult(false),
            _ =>
            {
                legacyProbeCount++;
                return Task.FromResult(true);
            });

        Assert.Equal(UninstallReconnectPolicy.RestoreImmediately, policy);
        Assert.Equal(0, legacyProbeCount);
    }

    [Fact]
    public async Task ManagedUninstallPreservesOnlyAVerifiedManagedEndpoint()
    {
        var policy = await Program.ResolveUninstallReconnectPolicyAsync(
            managedInstalledPort: 45123,
            legacyInstalledPort: null,
            configuredUrl: LoopbackEndpoint.WebSocketUrl(45123),
            _ => Task.FromResult(true),
            _ => Task.FromResult(false));

        Assert.Equal(UninstallReconnectPolicy.PreserveUntilNextSignIn, policy);
    }

    [Fact]
    public async Task LegacyUninstallUsesTheLegacyReadinessProbe()
    {
        var policy = await Program.ResolveUninstallReconnectPolicyAsync(
            managedInstalledPort: null,
            legacyInstalledPort: 45123,
            configuredUrl: LoopbackEndpoint.WebSocketUrl(45123),
            _ => Task.FromResult(false),
            _ => Task.FromResult(true));

        Assert.Equal(UninstallReconnectPolicy.PreserveUntilNextSignIn, policy);
    }

    [Fact]
    public async Task BlocksPortChangeWhileInstalledBackendIsReady()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Program.EnsurePortChangeIsSafeAsync(
                45123,
                45124,
                _ => Task.FromResult(true)));

        Assert.Contains("port 45123 is still ready", exception.Message);
        Assert.Contains("port 45124", exception.Message);
    }

    [Theory]
    [InlineData(null, 45124, true)]
    [InlineData(45123, 45123, true)]
    [InlineData(45123, 45124, false)]
    public async Task AllowsSafeInstallPortSelection(
        int? installedPort,
        int requestedPort,
        bool installedBackendReady)
    {
        var probeCount = 0;
        await Program.EnsurePortChangeIsSafeAsync(
            installedPort,
            requestedPort,
            _ =>
            {
                probeCount++;
                return Task.FromResult(installedBackendReady);
            });

        Assert.Equal(
            installedPort is not null && installedPort != requestedPort ? 1 : 0,
            probeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlocksMutationWhileSupervisorOwnsPort(bool uninstall)
    {
        var port = FindAvailablePort();
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, Program.SupervisorMutexName(port));
            mutex.WaitOne();
            try
            {
                ready.Set();
                release.Wait();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        });
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            AssertMutationBlocked(uninstall, port, "supervisor");
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupervisorFileLockBlocksEveryPortAndMutation(bool uninstall)
    {
        var port = FindAvailablePort();
        var otherPort = FindAvailablePort(port);
        Directory.CreateDirectory(root);
        using (var supervisorLock = new FileStream(
                   ContinuityPaths.SupervisorLockFile(root),
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            Assert.Equal(1, await Program.ServeAsync(
                otherPort,
                root,
                (_, _, _) => Task.CompletedTask));
            AssertMutationBlocked(uninstall, port, "supervisor");
        }

        Assert.Equal("reacquired", RunMutation(uninstall, port, () => "reacquired"));
    }

    [Fact]
    public async Task ServeRefusesTheSessionMutexAndReleasesTheFileLock()
    {
        var port = FindAvailablePort();
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, Program.SupervisorMutexName(port));
            mutex.WaitOne();
            try
            {
                ready.Set();
                release.Wait();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        });
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, await Program.ServeAsync(
                port,
                root,
                (_, _, _) => throw new InvalidOperationException("Updater must not start.")));
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(
            "file lock released",
            RunMutation(false, port, () => "file lock released"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationHoldsBothOwnershipGuardsDuringCallback(bool uninstall)
    {
        var port = FindAvailablePort();
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        var mutation = Task.Run(() => RunMutation(uninstall, port, () =>
        {
            callbackEntered.Set();
            releaseCallback.Wait();
            return "mutated";
        }));
        try
        {
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Throws<IOException>(() => new FileStream(
                ContinuityPaths.SupervisorLockFile(root),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None));
            var acquiredMutex = await Task.Run(() =>
            {
                using var mutex = new Mutex(initiallyOwned: false, Program.SupervisorMutexName(port));
                var acquired = mutex.WaitOne(0);
                if (acquired)
                {
                    mutex.ReleaseMutex();
                }
                return acquired;
            });
            Assert.False(acquiredMutex);
        }
        finally
        {
            releaseCallback.Set();
        }

        Assert.Equal("mutated", await mutation.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PortChangeRoutingUsesOwnershipBoundary(bool hasInstalledState)
    {
        var installedPort = hasInstalledState ? FindAvailablePort() : (int?)null;
        var requestedPort = FindAvailablePort(installedPort ?? 0);
        Directory.CreateDirectory(root);
        using var supervisorLock = new FileStream(
            ContinuityPaths.SupervisorLockFile(root),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var mutationRan = false;
        string Mutate()
        {
            mutationRan = true;
            return "mutated";
        }

        if (hasInstalledState)
        {
            Assert.Throws<InvalidOperationException>(() =>
                Program.RunInstallMutation(root, installedPort, requestedPort, Mutate));
            Assert.False(mutationRan);
        }
        else
        {
            Assert.Equal(
                "mutated",
                Program.RunInstallMutation(root, installedPort, requestedPort, Mutate));
            Assert.True(mutationRan);
        }
    }

    [Fact]
    public void SamePortInstallCanStageWhileSupervisorOwnsState()
    {
        var port = FindAvailablePort();
        Directory.CreateDirectory(root);
        using var supervisorLock = new FileStream(
            ContinuityPaths.SupervisorLockFile(root),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        Assert.Equal(
            "staged",
            Program.RunInstallMutation(root, port, port, () => "staged"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveLeaseBlocksMutation(bool uninstall)
    {
        var publicPort = FindAvailablePort();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var backendPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var current = Process.GetCurrentProcess();
        var store = new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root));
        store.Write(Lease(publicPort, backendPort, Environment.ProcessId,
            current.StartTime.ToUniversalTime()));
        var leaseBytes = File.ReadAllBytes(ContinuityPaths.BackendLeaseFile(root));

        AssertMutationBlocked(uninstall, publicPort, "persisted backend lease");

        Assert.Equal(leaseBytes, File.ReadAllBytes(ContinuityPaths.BackendLeaseFile(root)));
        Assert.False(current.HasExited);
        Assert.True(listener.Server.IsBound);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedLeaseBlocksMutationWithoutChangingState(bool uninstall)
    {
        Directory.CreateDirectory(root);
        var port = FindAvailablePort();
        var leasePath = ContinuityPaths.BackendLeaseFile(root);
        File.WriteAllText(leasePath, "{not-json");
        var leaseBytes = File.ReadAllBytes(leasePath);

        AssertMutationBlocked(uninstall, port, "persisted backend lease");

        Assert.Equal(leaseBytes, File.ReadAllBytes(leasePath));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MissingOrStaleLeaseAllowsMutation(bool uninstall, bool stale)
    {
        var publicPort = FindAvailablePort();
        var store = new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root));
        if (stale)
        {
            store.Write(Lease(
                publicPort,
                FindAvailablePort(publicPort),
                int.MaxValue,
                DateTimeOffset.UtcNow));
        }

        var result = RunMutation(uninstall, publicPort, () => "mutated");

        Assert.Equal("mutated", result);
        Assert.Equal(BackendLeaseLoadKind.Missing, store.Load().Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MutationFailureReleasesSupervisorOwnership(bool uninstall)
    {
        var port = FindAvailablePort();

        Assert.Throws<InjectedMutationException>(() =>
            RunMutation<string>(uninstall, port, () => throw new InjectedMutationException()));

        Assert.Equal("retried", RunMutation(uninstall, port, () => "retried"));
    }

    private T RunMutation<T>(bool uninstall, int port, Func<T> mutation) => uninstall
        ? Program.RunUninstallMutation(root, port, mutation)
        : Program.RunPortChangeMutation(root, port, mutation);

    private void AssertMutationBlocked(bool uninstall, int port, string expectedMessage)
    {
        var mutationRan = false;
        var exception = Assert.Throws<InvalidOperationException>(() =>
            RunMutation(uninstall, port, () => mutationRan = true));
        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(mutationRan);
    }

    private static BackendLease Lease(
        int publicPort,
        int backendPort,
        int backendProcessId,
        DateTimeOffset startedAt) => new(
            BackendLease.CurrentSchemaVersion,
            OwnerSupervisorProcessId: Environment.ProcessId,
            backendProcessId,
            publicPort,
            backendPort,
            Environment.ProcessPath!,
            FutureProcessEnvironment.ResolveCodexHome(),
            startedAt);

    private static int FindAvailablePort(params int[] excludedPorts)
    {
        while (true)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (!excludedPorts.Contains(port))
            {
                return port;
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class InjectedMutationException : Exception;
}
