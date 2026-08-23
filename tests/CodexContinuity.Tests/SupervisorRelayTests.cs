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
    public async Task SupervisorKeepsPublicEndpointSeparateFromPrivateBackend()
    {
        var publicPort = FindAvailablePort();
        var fixtureReadyPath = Path.Combine(root, "fixture-ready.txt");
        var privatePort = 0;
        var backendProcessId = 0;
        using var shutdown = new CancellationTokenSource();

        WindowsProcessGroup StartBackend(int port)
        {
            privatePort = port;
            var process = StartHarnessBackend(port, fixtureReadyPath);
            backendProcessId = process.Id;
            return process;
        }

        var supervisor = OwnedSupervisorRuntime.RunAsync(
            publicPort, root, shutdown.Token, StartBackend);
        try
        {
            var relayedBody = await ReadWhenReadyAsync(publicPort);
            var directBody = await ReadWhenReadyAsync(privatePort);
            var status = await ReadRunningStatusAsync();

            Assert.NotEqual(publicPort, privatePort);
            Assert.Equal($"backend:{privatePort}", relayedBody);
            Assert.Equal(relayedBody, directBody);
            Assert.Equal(
                new SupervisorStatus(
                    State: "running",
                    SupervisorProcessId: Environment.ProcessId,
                    BackendProcessId: backendProcessId,
                    Port: publicPort,
                    CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                    ConsecutiveFailures: 0,
                    LastExitCode: null,
                    UpdatedAtUtc: status.UpdatedAtUtc,
                    NextRetryAtUtc: null,
                    Detail:
                        $"Relaying {LoopbackEndpoint.WebSocketUrl(publicPort)} to an owned private backend.",
                    SupervisorStartedAtUtc: status.SupervisorStartedAtUtc,
                    SupervisorExecutable: status.SupervisorExecutable),
                status);
            Assert.False(CanBind(publicPort));
        }
        finally
        {
            shutdown.Cancel();
            Assert.Equal(0, await supervisor.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        Assert.False(ProcessIsRunning(backendProcessId));
        Assert.True(CanBind(publicPort));
        Assert.Equal(
            "stopped",
            new SupervisorStatusStore(ContinuityPaths.SupervisorStatusFile(root)).Read()?.State);
    }

    [Fact]
    public async Task BackendRestartKeepsExclusivePublicEndpoint()
    {
        var publicPort = FindAvailablePort();
        var backendPorts = new ConcurrentQueue<int>();
        var backendProcessIds = new ConcurrentQueue<int>();
        var generation = 0;
        using var shutdown = new CancellationTokenSource();

        WindowsProcessGroup StartBackend(int port)
        {
            var currentGeneration = Interlocked.Increment(ref generation);
            var process = StartHarnessBackend(
                port,
                Path.Combine(root, $"fixture-ready-{currentGeneration}.txt"),
                exitAfterRequests: currentGeneration == 1 ? 2 : 0);
            backendPorts.Enqueue(port);
            backendProcessIds.Enqueue(process.Id);
            return process;
        }

        var supervisor = OwnedSupervisorRuntime.RunAsync(
            publicPort, root, shutdown.Token, StartBackend);
        try
        {
            var firstBody = await ReadWhenReadyAsync(publicPort);
            Assert.False(CanBind(publicPort));
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
        }
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
        var harnessExecutable = Path.ChangeExtension(typeof(HarnessMarker).Assembly.Location, ".exe");
        var startInfo = new ProcessStartInfo(harnessExecutable)
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

    private static int FindAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
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
