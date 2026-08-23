using CodexContinuity;
using CodexContinuity.ProcessHarness;
using System.Diagnostics;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class SelfTestIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-self-test-fixture-{Guid.NewGuid():N}");

    [Fact]
    public async Task ProvesRelayedReconnectAndGracefulCleanup()
    {
        Directory.CreateDirectory(root);
        var processId = 0;

        var result = await Program.RunSelfTestAsync(
            startBackend: (port, _) => StartFakeBackend(port, "clean", out processId),
            boundedStopTimeout: TimeSpan.FromSeconds(5));

        Assert.Equal(
            $"{{\"passed\":true,\"isolated\":true,\"appServerPid\":{processId}," +
            "\"threadId\":\"fake-thread\",\"relayed\":true,\"reconnected\":true," +
            "\"threadPersistedAcrossReconnect\":true,\"boundedStop\":true," +
            "\"stopDisposition\":\"cleanExit\"}",
            result.ToJsonString());
        Assert.False(ProcessIsRunning(processId));
    }

    [Fact]
    public async Task BoundedStopTimeoutForcesBackendCleanup()
    {
        Directory.CreateDirectory(root);
        var processId = 0;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Program.RunSelfTestAsync(
                startBackend: (port, _) => StartFakeBackend(port, "ignore", out processId),
                boundedStopTimeout: TimeSpan.FromMilliseconds(100)));

        Assert.Equal(
            "The isolated app-server did not honor bounded Ctrl+Break shutdown.",
            exception.Message);
        Assert.False(ProcessIsRunning(processId));
    }

    [Fact]
    public async Task GateCloseFailureStillStopsBackend()
    {
        Directory.CreateDirectory(root);
        var publicPort = AvailablePort();
        var backendPort = AvailablePort(publicPort);
        await using var relay = LoopbackRelay.Start(publicPort, backendPort);
        using var process = StartFakeBackend(backendPort, "ignore", out _);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Program.CleanupSelfTestAsync(relay, process, canceled.Token));

        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task AlreadyExitedBackendIsNotReportedGraceful()
    {
        Directory.CreateDirectory(root);
        using var process = WindowsProcessGroup.Start(new ProcessStartInfo(HarnessExecutable())
        {
            UseShellExecute = false,
            WorkingDirectory = root,
        });
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            Program.AppServerStopDisposition.AlreadyExited,
            await Program.StopAppServerWithCtrlBreakAsync(
                process,
                TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task NonzeroExitAfterCtrlBreakIsNotReportedGraceful()
    {
        Directory.CreateDirectory(root);
        var port = AvailablePort();
        using var process = StartFakeBackend(port, "nonzero", out _);
        await WaitUntilReadyAsync(port);

        Assert.Equal(
            Program.AppServerStopDisposition.UnexpectedExit,
            await Program.StopAppServerWithCtrlBreakAsync(
                process,
                TimeSpan.FromSeconds(5)));
        Assert.Equal(17, process.ExitCode);
    }

    [Fact]
    public async Task WindowsControlExitIsReportedWithoutClaimingGracefulDrain()
    {
        Directory.CreateDirectory(root);
        var port = AvailablePort();
        using var process = StartFakeBackend(port, "control-exit", out _);
        await WaitUntilReadyAsync(port);

        Assert.Equal(
            Program.AppServerStopDisposition.WindowsControlExit,
            await Program.StopAppServerWithCtrlBreakAsync(
                process,
                TimeSpan.FromSeconds(5)));
        Assert.Equal(unchecked((int)0xC000013A), process.ExitCode);
    }

    [Fact]
    public async Task CallerCancellationDuringGracefulStopDoesNotBecomeTimeout()
    {
        Directory.CreateDirectory(root);
        var port = AvailablePort();
        var signalMarkerPath = Path.Combine(root, "signal.txt");
        using var process = StartFakeBackend(
            port,
            "ignore",
            out _,
            signalMarkerPath);
        try
        {
            await WaitUntilReadyAsync(port);
            using var cancellation = new CancellationTokenSource();
            var stop = Program.StopAppServerWithCtrlBreakAsync(
                process,
                TimeSpan.FromSeconds(5),
                cancellation.Token);
            Assert.True(SpinWait.SpinUntil(
                () => File.Exists(signalMarkerPath),
                TimeSpan.FromSeconds(5)));
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);

            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
        }
    }

    private WindowsProcessGroup StartFakeBackend(
        int port,
        string stopBehavior,
        out int processId,
        string? signalMarkerPath = null)
    {
        var startInfo = new ProcessStartInfo(HarnessExecutable())
        {
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        startInfo.ArgumentList.Add("fake-self-test-app-server");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add(stopBehavior);
        if (signalMarkerPath is not null)
        {
            startInfo.ArgumentList.Add(signalMarkerPath);
        }
        var process = WindowsProcessGroup.Start(startInfo);
        processId = process.Id;
        return process;
    }

    private static async Task WaitUntilReadyAsync(int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(LoopbackEndpoint.ReadyUrl(port));
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException)
            {
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("The fake app-server did not become ready.");
    }

    private static int AvailablePort(params int[] excludedPorts)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var listener = new System.Net.Sockets.TcpListener(
                System.Net.IPAddress.Loopback,
                port: 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            if (!excludedPorts.Contains(port))
            {
                return port;
            }
        }
        throw new InvalidOperationException("Could not allocate a distinct loopback port.");
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

    private static string HarnessExecutable() => Path.ChangeExtension(
        typeof(HarnessMarker).Assembly.Location,
        ".exe");

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
