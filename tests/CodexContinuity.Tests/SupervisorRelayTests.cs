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
            await AssertBackendNotReadyAsync(privatePort);
            await AssertEndpointUnavailableAsync(publicPort);
            Assert.False(CanBind(publicPort));

            await File.WriteAllTextAsync(startGatePath, "release");
            var relayedBody = await ReadWhenReadyAsync(publicPort);
            var directBody = await ReadWhenReadyAsync(privatePort);
            var status = await ReadStatusAsync("running");

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

    [Fact]
    public async Task BackendRestartKeepsPublicEndpointGatedUntilReplacementIsReady()
    {
        var publicPort = FindAvailablePort();
        var backendPorts = new ConcurrentQueue<int>();
        var backendProcessIds = new ConcurrentQueue<int>();
        var secondStartGatePath = Path.Combine(root, "second-start-gate.txt");
        var backoffEntered = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBackoff = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = 0;
        using var shutdown = new CancellationTokenSource();

        WindowsProcessGroup StartBackend(int port)
        {
            var currentGeneration = Interlocked.Increment(ref generation);
            var process = StartHarnessBackend(
                port,
                Path.Combine(root, $"fixture-started-{currentGeneration}.txt"),
                startGatePath: currentGeneration == 2 ? secondStartGatePath : null,
                exitAfterRequests: currentGeneration == 1 ? 2 : 0);
            backendPorts.Enqueue(port);
            backendProcessIds.Enqueue(process.Id);
            return process;
        }

        async Task<bool> WaitForRestart(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            backoffEntered.TrySetResult(delay);
            try
            {
                await releaseBackoff.Task.WaitAsync(cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        var supervisor = OwnedSupervisorRuntime.RunAsync(
            publicPort,
            root,
            shutdown.Token,
            StartBackend,
            delayForFailure: _ => TimeSpan.FromSeconds(10),
            waitForRestart: WaitForRestart);
        try
        {
            var firstBody = await ReadWhenReadyAsync(publicPort);
            Assert.Equal(
                TimeSpan.FromSeconds(10),
                await backoffEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            var backingOff = await ReadStatusAsync("backingOff");
            Assert.NotNull(backingOff.NextRetryAtUtc);
            Assert.StartsWith("App-server exited after ", backingOff.Detail);
            Assert.Equal(
                new SupervisorStatus(
                    State: "backingOff",
                    SupervisorProcessId: Environment.ProcessId,
                    BackendProcessId: null,
                    Port: publicPort,
                    CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                    ConsecutiveFailures: 1,
                    LastExitCode: 17,
                    UpdatedAtUtc: backingOff.UpdatedAtUtc,
                    NextRetryAtUtc: backingOff.NextRetryAtUtc,
                    Detail: backingOff.Detail,
                    SupervisorStartedAtUtc: backingOff.SupervisorStartedAtUtc,
                    SupervisorExecutable: backingOff.SupervisorExecutable),
                backingOff);
            Assert.InRange(
                backingOff.NextRetryAtUtc.Value - backingOff.UpdatedAtUtc,
                TimeSpan.FromSeconds(9.5),
                TimeSpan.FromSeconds(10));
            Assert.Equal(1, Volatile.Read(ref generation));
            Assert.False(ProcessIsRunning(backendProcessIds.Single()));
            await AssertEndpointUnavailableAsync(publicPort);
            Assert.False(CanBind(publicPort));

            releaseBackoff.SetResult(true);
            await WaitForFileAsync(Path.Combine(root, "fixture-started-2.txt"));
            var ports = backendPorts.ToArray();
            Assert.Equal(2, ports.Length);
            Assert.NotEqual(ports[0], ports[1]);
            Assert.Equal($"backend:{ports[0]}", firstBody);
            await AssertBackendNotReadyAsync(ports[1]);
            await AssertEndpointUnavailableAsync(publicPort);

            await File.WriteAllTextAsync(secondStartGatePath, "release");
            Assert.Equal($"backend:{ports[1]}", await ReadWhenReadyAsync(publicPort));
        }
        finally
        {
            shutdown.Cancel();
            releaseBackoff.TrySetResult(true);
            Assert.Equal(0, await supervisor.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        Assert.All(backendProcessIds, processId => Assert.False(ProcessIsRunning(processId)));
        Assert.True(CanBind(publicPort));
    }

    [Fact]
    public async Task CancellationDuringBackoffDoesNotStartReplacement()
    {
        var publicPort = FindAvailablePort();
        var backoffEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startCount = 0;
        var backendProcessId = 0;
        using var shutdown = new CancellationTokenSource();

        WindowsProcessGroup StartBackend(int port)
        {
            Interlocked.Increment(ref startCount);
            var process = StartHarnessBackend(
                port,
                Path.Combine(root, "fixture-started.txt"),
                exitAfterRequests: 1);
            backendProcessId = process.Id;
            return process;
        }

        async Task<bool> WaitForRestart(
            TimeSpan _,
            CancellationToken cancellationToken)
        {
            backoffEntered.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        var supervisor = OwnedSupervisorRuntime.RunAsync(
            publicPort,
            root,
            shutdown.Token,
            StartBackend,
            delayForFailure: _ => TimeSpan.FromMinutes(1),
            waitForRestart: WaitForRestart);
        await backoffEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Volatile.Read(ref startCount));
        Assert.Equal("backingOff", (await ReadStatusAsync("backingOff")).State);
        await AssertEndpointUnavailableAsync(publicPort);

        shutdown.Cancel();
        Assert.Equal(0, await supervisor.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(1, Volatile.Read(ref startCount));
        Assert.False(ProcessIsRunning(backendProcessId));
        Assert.True(CanBind(publicPort));
    }

    [Fact]
    public async Task ForeignPublicEndpointDoesNotStartBackend()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.ExclusiveAddressUse,
            true);
        listener.Start();
        var publicPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var startCount = 0;

        var exitCode = await OwnedSupervisorRuntime.RunAsync(
            publicPort,
            root,
            CancellationToken.None,
            _ =>
            {
                startCount++;
                throw new InvalidOperationException("The backend callback must not run.");
            });

        Assert.Equal(1, exitCode);
        Assert.Equal(0, startCount);
        var status = new SupervisorStatusStore(
            ContinuityPaths.SupervisorStatusFile(root)).Read();
        Assert.NotNull(status);
        Assert.Equal(
            new SupervisorStatus(
                State: "foreignEndpoint",
                SupervisorProcessId: Environment.ProcessId,
                BackendProcessId: null,
                Port: publicPort,
                CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                ConsecutiveFailures: 0,
                LastExitCode: null,
                UpdatedAtUtc: status.UpdatedAtUtc,
                NextRetryAtUtc: null,
                Detail:
                    "An endpoint not owned by this supervisor already uses the configured port.",
                SupervisorStartedAtUtc: status.SupervisorStartedAtUtc,
                SupervisorExecutable: status.SupervisorExecutable),
            status);
        Assert.True(listener.Server.IsBound);
        Assert.False(CanBind(publicPort));
        var accept = listener.AcceptTcpClientAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, publicPort);
        using var accepted = await accept.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(accepted.Connected);
    }

    private async Task<SupervisorStatus> ReadStatusAsync(string state)
    {
        var store = new SupervisorStatusStore(ContinuityPaths.SupervisorStatusFile(root));
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (store.Read() is { } status && status.State == state)
            {
                return status;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Supervisor did not publish {state} relay status.");
    }

    private WindowsProcessGroup StartHarnessBackend(
        int port,
        string fixtureStartedPath,
        string? startGatePath = null,
        int exitAfterRequests = 0)
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
        startInfo.ArgumentList.Add(exitAfterRequests.ToString());
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

    private static async Task AssertBackendNotReadyAsync(int port)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            $"not-ready:{port}",
            await response.Content.ReadAsStringAsync());
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
