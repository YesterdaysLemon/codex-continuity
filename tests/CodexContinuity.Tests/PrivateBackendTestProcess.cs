using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CodexContinuity.ProcessHarness;

namespace CodexContinuity.Tests;

internal sealed class PrivateBackendTestProcess : IAsyncDisposable
{
    private readonly string testDirectory;

    private PrivateBackendTestProcess(
        string testDirectory,
        string signalMarkerPath,
        int port,
        WindowsProcessGroup process)
    {
        this.testDirectory = testDirectory;
        SignalMarkerPath = signalMarkerPath;
        Port = port;
        Process = process;
    }

    internal string SignalMarkerPath { get; }

    internal int Port { get; }

    internal WindowsProcessGroup Process { get; }

    internal BackendLease CreateLease(int publicPort) => new(
        BackendLease.CurrentSchemaVersion,
        OwnerSupervisorProcessId: Environment.ProcessId,
        BackendProcessId: Process.Id,
        PublicPort: publicPort,
        BackendPort: Port,
        BackendExecutable: Process.ExecutablePath,
        CodexHome: null,
        BackendStartedAtUtc: Process.StartedAtUtc);

    internal static async Task<PrivateBackendTestProcess> StartAsync(
        string stopBehavior = "clean",
        params int[] excludedPorts)
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"codex-continuity-private-backend-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var signalMarkerPath = Path.Combine(testDirectory, "signal.txt");
        var port = AvailablePort(excludedPorts);
        WindowsProcessGroup? process = null;
        try
        {
            var startInfo = new ProcessStartInfo(HarnessExecutable())
            {
                UseShellExecute = false,
                WorkingDirectory = testDirectory,
            };
            startInfo.ArgumentList.Add("fake-self-test-app-server");
            startInfo.ArgumentList.Add(port.ToString());
            startInfo.ArgumentList.Add(stopBehavior);
            startInfo.ArgumentList.Add(signalMarkerPath);
            process = WindowsProcessGroup.Start(startInfo);
            for (var attempt = 0; attempt < 50; attempt++)
            {
                if (await Program.IsReadyAsync(port, TimeSpan.FromMilliseconds(200)))
                {
                    return new PrivateBackendTestProcess(
                        testDirectory,
                        signalMarkerPath,
                        port,
                        process);
                }
                await Task.Delay(50);
            }
            throw new TimeoutException("The private test backend did not become ready.");
        }
        catch
        {
            if (process is { HasExited: false })
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            Directory.Delete(testDirectory, recursive: true);
            throw;
        }
    }

    internal static int AvailablePort(params int[] excludedPorts)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (!excludedPorts.Contains(port))
            {
                return port;
            }
        }
        throw new InvalidOperationException("Could not allocate a distinct loopback port.");
    }

    public async ValueTask DisposeAsync()
    {
        if (!Process.HasExited)
        {
            Process.Kill();
            await Process.WaitForExitAsync();
        }
        Process.Dispose();
        Directory.Delete(testDirectory, recursive: true);
    }

    private static string HarnessExecutable() => Path.ChangeExtension(
        typeof(HarnessMarker).Assembly.Location,
        ".exe");
}
