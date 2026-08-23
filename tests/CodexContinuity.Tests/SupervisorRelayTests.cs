using CodexContinuity.ProcessHarness;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class SupervisorRelayTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-supervisor-relay-{Guid.NewGuid():N}");

    [Fact]
    public async Task BackendRestartKeepsExclusivePublicEndpoint()
    {
        Directory.CreateDirectory(root);
        var publicPort = FindAvailablePort();
        var backendPorts = new ConcurrentQueue<int>();
        var backendProcessIds = new ConcurrentQueue<int>();
        var generation = 0;
        WindowsProcessGroup? foreign = null;
        using var shutdown = new CancellationTokenSource();

        WindowsProcessGroup StartBackend(int port)
        {
            var currentGeneration = Interlocked.Increment(ref generation);
            var process = StartHarnessBackend(
                port,
                Path.Combine(root, $"fixture-ready-{currentGeneration}.txt"),
                exitAfterRequests: currentGeneration == 1 ? -2 : 0);
            backendPorts.Enqueue(port);
            backendProcessIds.Enqueue(process.Id);
            return process;
        }

        var supervisor = Program.RunOwnedSupervisorAsync(
            publicPort,
            root,
            shutdown.Token,
            StartBackend);
        try
        {
            var firstBody = await ReadWhenReadyAsync(publicPort);
            Assert.False(CanBind(publicPort));
            var firstPort = backendPorts.First();
            Assert.True(SpinWait.SpinUntil(
                () => CanBind(firstPort),
                TimeSpan.FromSeconds(5)));
            foreign = StartHarnessBackend(firstPort, Path.Combine(root, "foreign-ready.txt"));
            Assert.Equal($"backend:{firstPort}", await ReadWhenReadyAsync(firstPort));
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) })
            {
                await Assert.ThrowsAsync<HttpRequestException>(() =>
                    client.GetAsync(LoopbackEndpoint.ReadyUrl(publicPort)));
            }
            var secondBody = await ReadWhenBodyChangesAsync(publicPort, firstBody);
            var ports = backendPorts.ToArray();
            var processIds = backendProcessIds.ToArray();

            Assert.True(ports.Length >= 2);
            Assert.NotEqual(ports[0], ports[1]);
            Assert.Equal($"backend:{ports[0]}", firstBody);
            Assert.Equal($"backend:{ports[1]}", secondBody);
            Assert.False(ProcessIsRunning(processIds[0]));
            Assert.False(CanBind(publicPort));
        }
        finally
        {
            shutdown.Cancel();
            Assert.Equal(0, await supervisor.WaitAsync(TimeSpan.FromSeconds(10)));
            if (foreign is { HasExited: false })
            {
                foreign.Kill();
                await foreign.WaitForExitAsync();
            }
            foreign?.Dispose();
        }
    }

    [Fact]
    public async Task ForeignPrivateListenerNeverOpensPublicGate()
    {
        Directory.CreateDirectory(root);
        var publicPort = FindAvailablePort();
        var privatePort = 0;
        var trackedProcessId = 0;
        WindowsProcessGroup? foreign = null;
        using var shutdown = new CancellationTokenSource();

        WindowsProcessGroup StartBackend(int port)
        {
            privatePort = port;
            foreign = StartHarnessBackend(port, Path.Combine(root, "foreign-ready.txt"));
            var startInfo = new ProcessStartInfo(HarnessExecutable())
            {
                UseShellExecute = false,
                WorkingDirectory = root,
            };
            startInfo.ArgumentList.Add("process-group-child");
            startInfo.ArgumentList.Add(root);
            startInfo.ArgumentList.Add("tracked-non-listener");
            startInfo.ArgumentList.Add("-1");
            var tracked = WindowsProcessGroup.Start(startInfo);
            trackedProcessId = tracked.Id;
            return tracked;
        }

        var supervisor = Program.RunOwnedSupervisorAsync(
            publicPort,
            root,
            shutdown.Token,
            StartBackend);
        try
        {
            Assert.Equal($"backend:{privatePort}", await ReadWhenReadyAsync(privatePort));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.GetAsync(LoopbackEndpoint.ReadyUrl(publicPort)));
            Assert.NotNull(foreign);
            Assert.False(foreign.HasExited);
        }
        finally
        {
            shutdown.Cancel();
            Assert.Equal(0, await supervisor.WaitAsync(TimeSpan.FromSeconds(10)));
            if (foreign is { HasExited: false })
            {
                foreign.Kill();
                await foreign.WaitForExitAsync();
            }
            foreign?.Dispose();
        }

        Assert.False(ProcessIsRunning(trackedProcessId));
        Assert.True(CanBind(publicPort));
    }

    [Fact]
    public async Task RecoversVerifiedBackendWithoutStartingReplacement()
    {
        Directory.CreateDirectory(root);
        var publicPort = FindAvailablePort();
        var backendPort = FindAvailablePort(publicPort);
        var readyPath = Path.Combine(root, "recovered-ready.txt");
        var original = StartHarnessBackend(backendPort, readyPath);
        var backendProcessId = original.Id;
        await ReadWhenReadyAsync(backendPort);
        new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root)).Write(
            new BackendLease(
                BackendLease.CurrentSchemaVersion,
                OwnerSupervisorProcessId: int.MaxValue,
                BackendProcessId: backendProcessId,
                PublicPort: publicPort,
                BackendPort: backendPort,
                BackendExecutable: original.ExecutablePath,
                CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                BackendStartedAtUtc: original.StartedAtUtc));
        original.Dispose();
        var replacementsStarted = 0;
        using var shutdown = new CancellationTokenSource();

        var supervisor = Program.RunOwnedSupervisorAsync(
            publicPort,
            root,
            shutdown.Token,
            port =>
            {
                replacementsStarted++;
                return StartHarnessBackend(
                    port,
                    Path.Combine(root, "unexpected-replacement.txt"));
            });
        try
        {
            Assert.Equal($"backend:{backendPort}", await ReadWhenReadyAsync(publicPort));
            var status = await ReadRunningStatusAsync();
            Assert.Equal(backendProcessId, status.BackendProcessId);
            Assert.Equal(
                "Recovered the verified private backend behind the stable endpoint.",
                status.Detail);
            Assert.Equal(0, replacementsStarted);
        }
        finally
        {
            shutdown.Cancel();
            Assert.Equal(0, await supervisor.WaitAsync(TimeSpan.FromSeconds(10)));
            if (ProcessIsRunning(backendProcessId))
            {
                using var cleanup = Process.GetProcessById(backendProcessId);
                cleanup.Kill(entireProcessTree: true);
                await cleanup.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        Assert.False(ProcessIsRunning(backendProcessId));
        Assert.Equal(
            BackendLeaseLoadKind.Missing,
            new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root)).Load().Kind);
    }

    private async Task<SupervisorStatus> ReadRunningStatusAsync()
    {
        var store = new SupervisorStatusStore(ContinuityPaths.SupervisorStatusFile(root));
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (store.Read() is { State: "running" } status)
            {
                return status;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("Supervisor did not publish running relay status.");
    }

    private WindowsProcessGroup StartHarnessBackend(
        int port,
        string fixtureReadyPath,
        int exitAfterRequests = 0)
    {
        var startInfo = new ProcessStartInfo(HarnessExecutable())
        {
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        startInfo.ArgumentList.Add("fake-app-server");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add(fixtureReadyPath);
        startInfo.ArgumentList.Add(exitAfterRequests.ToString());
        return WindowsProcessGroup.Start(startInfo);
    }

    private static string HarnessExecutable() => Path.ChangeExtension(
        typeof(HarnessMarker).Assembly.Location,
        ".exe");

    private async Task<string> ReadWhenReadyAsync(int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                return await client.GetStringAsync($"http://127.0.0.1:{port}/readyz");
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(100);
            }
        }
        var status = new SupervisorStatusStore(
            ContinuityPaths.SupervisorStatusFile(root)).Read();
        var logPath = ContinuityPaths.AppServerLogFile(root);
        var log = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : "<no log>";
        throw new TimeoutException(
            $"Endpoint on port {port} did not become ready. Status: {status}. Log: {log}");
    }

    private async Task<string> ReadWhenBodyChangesAsync(int port, string previousBody)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                var body = await client.GetStringAsync($"http://127.0.0.1:{port}/readyz");
                if (body != previousBody)
                {
                    return body;
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException)
            {
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("Public relay did not expose the restarted backend.");
    }

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

    private static bool CanBind(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Server.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ExclusiveAddressUse,
                true);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static bool ProcessIsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
