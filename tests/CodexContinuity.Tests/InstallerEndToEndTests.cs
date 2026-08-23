using CodexContinuity;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class InstallerEndToEndTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-bootstrap-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task NativeAndPowerShellBootstrapsReturnWhileStartedChildRemainsAlive()
    {
        Directory.CreateDirectory(root);
        var recordPath = Path.Combine(root, "installer-record.txt");
        var archive = CreateFixtureArchive(recordPath);
        var checksum = Encoding.ASCII.GetBytes(
            $"{Convert.ToHexString(SHA256.HashData(archive))}  CodexContinuity-win-x64.zip");
        await using var server = new StaticHttpServer(new Dictionary<string, byte[]>
        {
            ["/CodexContinuity-win-x64.zip"] = archive,
            ["/CodexContinuity-win-x64.zip.sha256"] = checksum,
        });
        var setupDirectoriesBefore = TemporaryDirectories("codex-continuity-setup-*");
        var installDirectoriesBefore = TemporaryDirectories("codex-continuity-install-*");

        try
        {
            var nativeExitCode = await BootstrapInstaller.RunAsync(
                45124,
                TrayInstallMode.Enabled,
                startNow: true,
                skipSelfTest: false,
                quiet: true,
                server.BaseUrl).WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Equal(0, nativeExitCode);
            var powershell = StartPowerShellInstaller(server.BaseUrl, port: 45125);
            using (powershell.Process)
            {
                await powershell.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                await Task.WhenAll(powershell.Output, powershell.Error)
                    .WaitAsync(TimeSpan.FromSeconds(3));
                var standardOutput = await powershell.Output;
                var standardError = await powershell.Error;
                Assert.True(
                    powershell.Process.ExitCode == 0,
                    $"PowerShell bootstrap failed: {standardError}{standardOutput}");
                Assert.Equal(string.Empty, standardError);
            }

            var records = await File.ReadAllLinesAsync(recordPath);
            Assert.Contains("self-test", records);
            Assert.Contains("install --port 45124 --start-now", records);
            Assert.Contains("install --port 45125 --start-now", records);
            Assert.Equal(2, records.Count(line => line == "self-test"));
            Assert.Equal(2, records.Count(line => line.StartsWith("child-pid:", StringComparison.Ordinal)));
            foreach (var line in records.Where(
                         line => line.StartsWith("child-pid:", StringComparison.Ordinal)))
            {
                using var child = Process.GetProcessById(ParseChildProcessId(line));
                Assert.False(child.HasExited);
            }
            Assert.True(setupDirectoriesBefore.SetEquals(
                TemporaryDirectories("codex-continuity-setup-*")));
            Assert.True(installDirectoriesBefore.SetEquals(
                TemporaryDirectories("codex-continuity-install-*")));
        }
        finally
        {
            await KillRecordedChildrenAsync(recordPath);
        }
    }

    [Fact]
    public async Task NativeBootstrapRejectsBadChecksumBeforeExecutingArchive()
    {
        Directory.CreateDirectory(root);
        var recordPath = Path.Combine(root, "bad-checksum-record.txt");
        var archive = CreateFixtureArchive(recordPath);
        await using var server = new StaticHttpServer(new Dictionary<string, byte[]>
        {
            ["/CodexContinuity-win-x64.zip"] = archive,
            ["/CodexContinuity-win-x64.zip.sha256"] =
                Encoding.ASCII.GetBytes($"{new string('0', 64)}  CodexContinuity-win-x64.zip"),
        });
        var setupDirectoriesBefore = TemporaryDirectories("codex-continuity-setup-*");

        await Assert.ThrowsAsync<InvalidDataException>(() => BootstrapInstaller.RunAsync(
            45123,
            TrayInstallMode.Enabled,
            startNow: true,
            skipSelfTest: false,
            quiet: true,
            server.BaseUrl));

        Assert.False(File.Exists(recordPath));
        Assert.True(setupDirectoriesBefore.SetEquals(
            TemporaryDirectories("codex-continuity-setup-*")));
    }

    [Fact]
    public async Task DownloadRejectsOversizedContentLength()
    {
        Directory.CreateDirectory(root);
        await using var server = new StaticHttpServer(new Dictionary<string, byte[]>
        {
            ["/oversized"] = new byte[5],
        });
        var destination = Path.Combine(root, "oversized.bin");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BootstrapInstaller.DownloadAsync(
                $"{server.BaseUrl}/oversized",
                destination,
                maximumBytes: 4,
                CancellationToken.None));

        Assert.Contains("declares 5 bytes", exception.Message);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadRejectsStreamedOverflowWithoutContentLength()
    {
        Directory.CreateDirectory(root);
        await using var server = new StaticHttpServer(
            new Dictionary<string, byte[]>
            {
                ["/streamed"] = new byte[5],
            },
            includeContentLength: false);
        var destination = Path.Combine(root, "streamed.bin");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BootstrapInstaller.DownloadAsync(
                $"{server.BaseUrl}/streamed",
                destination,
                maximumBytes: 4,
                CancellationToken.None));

        Assert.Contains("4-byte safety limit", exception.Message);
        Assert.True(File.Exists(destination));
        Assert.InRange(new FileInfo(destination).Length, 0, 4);
    }

    [Fact]
    public async Task SupervisorRefusesToAdoptForeignReadyEndpoint()
    {
        Directory.CreateDirectory(root);
        await using var server = new StaticHttpServer(new Dictionary<string, byte[]>
        {
            ["/readyz"] = "ready"u8.ToArray(),
        });

        var exitCode = await Program.ServeAsync(server.Port, root);

        Assert.Equal(1, exitCode);
        var status = new SupervisorStatusStore(
            ContinuityPaths.SupervisorStatusFile(root)).Read();
        Assert.NotNull(status);
        Assert.Equal(
            new SupervisorStatus(
                State: "foreignEndpoint",
                SupervisorProcessId: Environment.ProcessId,
                BackendProcessId: null,
                Port: server.Port,
                CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                ConsecutiveFailures: 0,
                LastExitCode: null,
                UpdatedAtUtc: status.UpdatedAtUtc,
                NextRetryAtUtc: null,
                Detail: "An endpoint not owned by this supervisor already uses the configured port.",
                SupervisorStartedAtUtc: Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                SupervisorExecutable: Environment.ProcessPath),
            status);
    }

    [Fact]
    public async Task SupervisorCancelsAndAwaitsUpdaterOnEarlyExit()
    {
        Directory.CreateDirectory(root);
        await using var server = new StaticHttpServer(new Dictionary<string, byte[]>
        {
            ["/readyz"] = "ready"u8.ToArray(),
        });
        var updaterStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowUpdaterExit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RunUpdater(
            string _stateDirectory,
            string _runningVersion,
            CancellationToken cancellationToken)
        {
            updaterStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.SetResult();
            }
            await allowUpdaterExit.Task;
        }

        var serveTask = Program.ServeAsync(server.Port, root, RunUpdater);
        await updaterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(serveTask.IsCompleted);
        Assert.Throws<InvalidOperationException>(() =>
            Program.RunPortChangeMutation(root, server.Port, () => "unexpected"));
        Assert.Throws<InvalidOperationException>(() =>
            Program.RunUninstallMutation(root, server.Port, () => "unexpected"));
        allowUpdaterExit.SetResult();
        Assert.Equal(1, await serveTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            "port changed",
            Program.RunPortChangeMutation(root, server.Port, () => "port changed"));
        Assert.Equal(
            "uninstalled",
            Program.RunUninstallMutation(root, server.Port, () => "uninstalled"));
    }

    [Fact]
    public async Task SupervisorUsesOwnedRelayRuntimeWithinUpdateLifetime()
    {
        Directory.CreateDirectory(root);
        var publicPort = Program.FindAvailablePort();
        var updaterCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimeEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRuntimeExit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var capturedPort = 0;
        string? capturedStateDirectory = null;
        var capturedShutdownToken = default(CancellationToken);
        Func<int, WindowsProcessGroup>? capturedStartBackend = null;

        async Task RunUpdater(
            string _stateDirectory,
            string _runningVersion,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                updaterCancelled.SetResult();
            }
        }

        async Task<int> RunOwnedSupervisor(
            int port,
            string stateDirectory,
            CancellationToken shutdownToken,
            Func<int, WindowsProcessGroup> startBackend)
        {
            capturedPort = port;
            capturedStateDirectory = stateDirectory;
            capturedShutdownToken = shutdownToken;
            capturedStartBackend = startBackend;
            runtimeEntered.SetResult();
            await allowRuntimeExit.Task;
            return 23;
        }

        var serveTask = Program.ServeAsync(
            publicPort,
            root,
            SupervisorCompatibilityScope.ForStateDirectory(root),
            RunUpdater,
            RunOwnedSupervisor);
        var exitCode = 0;
        try
        {
            await runtimeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(serveTask.IsCompleted);
            Assert.False(capturedShutdownToken.IsCancellationRequested);
            Assert.False(updaterCancelled.Task.IsCompleted);
        }
        finally
        {
            allowRuntimeExit.TrySetResult();
            exitCode = await serveTask.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(23, exitCode);
        Assert.Equal(publicPort, capturedPort);
        Assert.Equal(root, capturedStateDirectory);
        Assert.NotNull(capturedStartBackend);
        Assert.True(capturedShutdownToken.IsCancellationRequested);
        await updaterCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        for (var attempt = 1; Directory.Exists(root); attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException) when (attempt < 6)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }
    }

    private (Process Process, Task<string> Output, Task<string> Error) StartPowerShellInstaller(
        string downloadBaseUrl,
        int port)
    {
        var script = FindRepositoryFile("install.ps1");
        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo(
            FindExecutableOnPath("pwsh.exe") ?? windowsPowerShell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile",
                     "-ExecutionPolicy",
                     "Bypass",
                     "-File",
                     script,
                     "-DownloadBaseUrl",
                     downloadBaseUrl,
                     "-Port",
                     port.ToString(),
                     "-StartNow",
                     "-Json",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start PowerShell bootstrap test.");
        return (
            process,
            process.StandardOutput.ReadToEndAsync(),
            process.StandardError.ReadToEndAsync());
    }

    private static string? FindExecutableOnPath(string executableName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => Path.Combine(entry.Trim().Trim('"'), executableName))
            .FirstOrDefault(File.Exists);

    private byte[] CreateFixtureArchive(string recordPath)
    {
        var repositoryDirectory = Path.GetDirectoryName(FindRepositoryFile("CodexContinuity.csproj"))
            ?? throw new InvalidOperationException("Repository project has no parent directory.");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Test output has no configuration directory.");
        var fixtureDirectory = Path.Combine(
            repositoryDirectory,
            "tests",
            "CodexContinuity.InstallerFixture",
            "bin",
            configuration,
            "net10.0-windows");
        var sources = new[]
        {
            "CodexContinuity.exe",
            "CodexContinuity.dll",
            "CodexContinuity.deps.json",
            "CodexContinuity.runtimeconfig.json",
        };

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var source in sources)
            {
                archive.CreateEntryFromFile(Path.Combine(fixtureDirectory, source), source);
            }
            WriteEntry(archive, "record-path.txt", Encoding.UTF8.GetBytes(recordPath));
            WriteEntry(archive, "CodexContinuity.Tray.exe", "fixture tray"u8.ToArray());
        }
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(content);
    }

    private static string FindRepositoryFile(string filename)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, filename);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException($"Could not find repository file {filename}.");
    }

    private static HashSet<string> TemporaryDirectories(string pattern) =>
        Directory.EnumerateDirectories(Path.GetTempPath(), pattern)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static int ParseChildProcessId(string line) =>
        int.Parse(line["child-pid:".Length..]);

    private static async Task KillRecordedChildrenAsync(string recordPath)
    {
        if (!File.Exists(recordPath))
        {
            return;
        }
        foreach (var line in await File.ReadAllLinesAsync(recordPath))
        {
            if (!line.StartsWith("child-pid:", StringComparison.Ordinal))
            {
                continue;
            }
            try
            {
                using var child = Process.GetProcessById(ParseChildProcessId(line));
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
            }
            catch (ArgumentException)
            {
            }
        }
    }

    private sealed class StaticHttpServer : IAsyncDisposable
    {
        private readonly IReadOnlyDictionary<string, byte[]> responses;
        private readonly bool includeContentLength;
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource shutdown = new();
        private readonly Task serverTask;

        internal StaticHttpServer(
            IReadOnlyDictionary<string, byte[]> responses,
            bool includeContentLength = true)
        {
            this.responses = responses;
            this.includeContentLength = includeContentLength;
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            serverTask = ServeAsync();
        }

        internal string BaseUrl { get; }

        internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public async ValueTask DisposeAsync()
        {
            shutdown.Cancel();
            listener.Stop();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            shutdown.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!shutdown.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(shutdown.Token);
                await RespondAsync(client, shutdown.Token);
            }
        }

        private async Task RespondAsync(TcpClient client, CancellationToken cancellationToken)
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken);
            string? header;
            do
            {
                header = await reader.ReadLineAsync(cancellationToken);
            }
            while (!string.IsNullOrEmpty(header));

            var path = requestLine?.Split(' ').ElementAtOrDefault(1) ?? string.Empty;
            var found = responses.TryGetValue(path, out var body);
            body ??= "Not found"u8.ToArray();
            var status = found ? "200 OK" : "404 Not Found";
            var contentLength = includeContentLength
                ? $"Content-Length: {body.Length}\r\n"
                : string.Empty;
            var responseHeaders = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\n{contentLength}Connection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
        }
    }
}
