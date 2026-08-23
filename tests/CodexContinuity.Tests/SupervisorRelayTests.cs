using CodexContinuity.ProcessHarness;
using System.Collections.Concurrent;
using System.ComponentModel;
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
    private readonly ConcurrentQueue<HarnessIdentity> harnesses = new();

    [Fact]
    public async Task GatesPublicEndpointUntilPrivateBackendIsReady()
    {
        var publicPort = FindAvailablePort();
        var fixtureStartedPath = Path.Combine(root, "fixture-started.txt");
        var startGatePath = Path.Combine(root, "start-gate.txt");
        var privatePort = 0;
        var backendProcessId = 0;
        using var shutdown = new CancellationTokenSource();

        WindowsProcessGroup StartBackend(int port)
        {
            privatePort = port;
            var process = StartHarnessBackend(port, fixtureStartedPath, startGatePath);
            backendProcessId = process.Id;
            return process;
        }

        var supervisor = OwnedSupervisorRuntime.RunAsync(
            publicPort, root, shutdown.Token, StartBackend);
        try
        {
            await WaitForFileAsync(fixtureStartedPath);
            using (var client = new HttpClient())
            using (var response = await client.GetAsync(
                $"http://127.0.0.1:{privatePort}/readyz"))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Equal(
                    $"not-ready:{privatePort}",
                    await response.Content.ReadAsStringAsync());
            }
            await AssertEndpointUnavailableAsync(publicPort);
            Assert.False(CanBind(publicPort));

            await File.WriteAllTextAsync(startGatePath, "release");
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
        }
        finally
        {
            shutdown.Cancel();
            await supervisor.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.False(ProcessIsRunning(backendProcessId));
        Assert.True(CanBind(publicPort));
        Assert.Equal(
            "stopped",
            new SupervisorStatusStore(ContinuityPaths.SupervisorStatusFile(root)).Read()?.State);
    }

    [Fact]
    public async Task StatusWriteFailureStopsOwnedBackend()
    {
        Directory.CreateDirectory(ContinuityPaths.SupervisorStatusFile(root));
        var publicPort = FindAvailablePort();
        var backendProcessId = 0;

        WindowsProcessGroup StartBackend(int port)
        {
            var process = StartHarnessBackend(port, Path.Combine(root, "fixture-started.txt"));
            backendProcessId = process.Id;
            return process;
        }

        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await OwnedSupervisorRuntime.RunAsync(
                publicPort, root, CancellationToken.None, StartBackend)
                .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.False(ProcessIsRunning(backendProcessId));
        Assert.True(CanBind(publicPort));
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
        string fixtureStartedPath,
        string? startGatePath = null)
    {
        var executable = HarnessExecutable();
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        startInfo.ArgumentList.Add("fake-app-server");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add(fixtureStartedPath);
        startInfo.ArgumentList.Add("0");
        if (startGatePath is not null)
        {
            startInfo.ArgumentList.Add(startGatePath);
        }
        var process = WindowsProcessGroup.Start(startInfo);
        harnesses.Enqueue(new(process.Id, process.StartedAtUtc, executable));
        return process;
    }

    private static string HarnessExecutable() => Path.ChangeExtension(
        typeof(HarnessMarker).Assembly.Location,
        ".exe");

    private static async Task WaitForFileAsync(string path)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (File.Exists(path))
            {
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Fixture did not create {path}.");
    }

    private static async Task AssertEndpointUnavailableAsync(int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.GetAsync($"http://127.0.0.1:{port}/readyz"));
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
        foreach (var identity in harnesses)
        {
            StopHarnessIfMatching(identity);
        }
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void StopHarnessIfMatching(HarnessIdentity identity)
    {
        try
        {
            using var process = WindowsProcessGroup.Attach(identity.ProcessId);
            if (process.StartedAtUtc != identity.StartedAtUtc ||
                !StringComparer.OrdinalIgnoreCase.Equals(
                    process.ExecutablePath,
                    identity.ExecutablePath))
            {
                return;
            }
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5))
                    .GetAwaiter().GetResult();
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
        }
    }

    private sealed record HarnessIdentity(
        int ProcessId,
        DateTimeOffset StartedAtUtc,
        string ExecutablePath);
}
