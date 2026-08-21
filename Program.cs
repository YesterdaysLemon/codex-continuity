using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexContinuity;

internal static class Program
{
    private const int DefaultPort = LoopbackEndpoint.DefaultPort;
    private const string UpdateManifestUrl =
        "https://persistent.oaistatic.com/codex-app-prod/windows-store-update.json";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
            var port = ParsePort(args) ?? DefaultPort;
            return command switch
            {
                "help" or "--help" or "-h" => PrintHelp(),
                "probe" => await ProbeAsync(port),
                "status" => await PrintStatusAsync(port),
                "serve" => await ServeAsync(port),
                "install" => await InstallAsync(
                    port,
                    args.Contains("--start-now", StringComparer.OrdinalIgnoreCase),
                    args.Contains("--no-tray", StringComparer.OrdinalIgnoreCase)
                        ? TrayInstallMode.Disabled
                        : TrayInstallMode.Enabled),
                "uninstall" => Uninstall(),
                "rollback" => Rollback(),
                "self-test" => await SelfTestAsync(),
                _ => Fail($"Unknown command '{command}'. Run 'CodexContinuity help'."),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Codex Continuity: {exception.Message}");
            return 1;
        }
    }

    private static int PrintHelp()
    {
        Console.WriteLine(
            """
            Codex Continuity keeps the Codex app-server alive independently of the desktop UI.

            Commands:
              probe       Inspect the installed desktop, update manifest, and backend configuration.
              status      Check backend health and count active threads.
              serve       Supervise a loopback WebSocket app-server.
              install     Configure future desktop launches and start at user logon.
              uninstall   Remove the user-level launch and environment configuration.
              rollback    Stage the previous known-good build for the next safe start.
              self-test   Prove reconnect and persisted-thread behavior in an isolated Codex home.

            Options:
              --port N       Loopback port (default: 45123).
              --start-now    With install, start the supervisor without touching the desktop app.
              --no-tray      With install, omit the disposable notification-area controller.

            Installation never closes or restarts the running Codex desktop app.
            """);
        return 0;
    }

    private static int? ParsePort(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].Equals("--port", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var port))
            {
                throw new ArgumentException("--port requires a valid TCP port.");
            }
            LoopbackEndpoint.ValidatePort(port);
            return port;
        }

        return null;
    }

    private static async Task<int> ProbeAsync(int port)
    {
        var codexPath = FindCodexExecutable();
        var package = await ReadInstalledPackageAsync();
        var manifest = await ReadUpdateManifestAsync();
        var configuredUrl = Environment.GetEnvironmentVariable(
            InstallCoordinator.AppServerUrlVariable,
            EnvironmentVariableTarget.User);
        var updaterDisabled = Environment.GetEnvironmentVariable(
            InstallCoordinator.DisableUpdaterVariable,
            EnvironmentVariableTarget.User);
        var healthy = await IsReadyAsync(port, TimeSpan.FromSeconds(1));
        var stateDirectory = ContinuityPaths.StateDirectory;
        var installState = new InstallStateStore(
            ContinuityPaths.InstallStateFile(stateDirectory)).Load();

        var result = new JsonObject
        {
            ["codexExecutable"] = codexPath,
            ["installedPackage"] = package,
            ["availableUpdate"] = manifest,
            ["updateAvailable"] = CompareVersions(
                package?["version"]?.GetValue<string>(),
                manifest?["buildVersion"]?.GetValue<string>()) < 0,
            ["appServerUrlForFutureLaunches"] = configuredUrl,
            ["inAppUpdaterDisabledForFutureLaunches"] = updaterDisabled == "false",
            ["continuityBackendReady"] = healthy,
            ["continuityBackendUrl"] = LoopbackEndpoint.WebSocketUrl(port),
            ["continuityInstallState"] = installState is null
                ? null
                : JsonSerializer.SerializeToNode(installState, JsonOptions),
        };
        Console.WriteLine(result.ToJsonString(JsonOptions));
        return 0;
    }

    private static async Task<int> PrintStatusAsync(int port)
    {
        if (!await IsReadyAsync(port, TimeSpan.FromSeconds(2)))
        {
            return Fail($"No ready continuity backend at {LoopbackEndpoint.WebSocketUrl(port)}.");
        }

        await using var client = await RpcClient.ConnectAsync(LoopbackEndpoint.WebSocketUrl(port));
        var threads = await client.ListThreadsAsync();
        var active = threads.Where(thread =>
            string.Equals(thread.Status, "active", StringComparison.OrdinalIgnoreCase)).ToList();
        var result = new JsonObject
        {
            ["ready"] = true,
            ["threadCount"] = threads.Count,
            ["activeThreadCount"] = active.Count,
            ["activeThreads"] = new JsonArray(active.Select(thread => new JsonObject
            {
                ["id"] = thread.Id,
                ["name"] = thread.Name,
                ["status"] = thread.Status,
            }).ToArray()),
            ["supervisor"] = JsonSerializer.SerializeToNode(
                new SupervisorStatusStore(ContinuityPaths.SupervisorStatusFile(
                    ContinuityPaths.StateDirectory)).Read(),
                JsonOptions),
        };
        Console.WriteLine(result.ToJsonString(JsonOptions));
        return 0;
    }

    private static async Task<int> ServeAsync(int port)
    {
        using var mutex = new Mutex(
            initiallyOwned: true,
            $"Local\\CodexContinuity-{port}",
            out var ownsMutex);
        if (!ownsMutex)
        {
            return Fail($"A Codex Continuity supervisor already owns port {port}.");
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        var stateDirectory = ContinuityPaths.StateDirectory;
        Directory.CreateDirectory(stateDirectory);
        var logPath = ContinuityPaths.AppServerLogFile(stateDirectory);
        var logWriter = new RollingLogWriter(logPath);
        var statusStore = new SupervisorStatusStore(
            ContinuityPaths.SupervisorStatusFile(stateDirectory));
        var backoff = new RestartBackoffPolicy();
        var consecutiveFailures = 0;
        Console.WriteLine(
            $"Supervising {LoopbackEndpoint.WebSocketUrl(port)} with logs at {logPath}");

        while (!shutdown.IsCancellationRequested)
        {
            if (await IsReadyAsync(port, TimeSpan.FromMilliseconds(500)))
            {
                try
                {
                    await using var existing = await RpcClient.ConnectAsync(
                        LoopbackEndpoint.WebSocketUrl(port));
                    statusStore.Write(NewSupervisorStatus(
                        "attached",
                        backendProcessId: null,
                        consecutiveFailures,
                        lastExitCode: null,
                        nextRetryAtUtc: null,
                        "A compatible app-server already owns the configured endpoint."));
                    Console.WriteLine("A compatible app-server already owns the configured endpoint.");
                    return 0;
                }
                catch (Exception exception) when (
                    exception is WebSocketException or InvalidOperationException or TaskCanceledException)
                {
                    throw new InvalidOperationException(
                        "The configured loopback port responds to readiness checks but is not a compatible app-server.",
                        exception);
                }
            }

            var codexPath = FindCodexExecutable();
            using var process = StartAppServer(codexPath, port);
            var startedAt = DateTimeOffset.UtcNow;
            var stdout = PumpLogAsync(process.StandardOutput, logWriter, shutdown.Token);
            var stderr = PumpLogAsync(process.StandardError, logWriter, shutdown.Token);

            if (!await WaitUntilReadyAsync(port, process, TimeSpan.FromSeconds(20), shutdown.Token))
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                await process.WaitForExitAsync();
            }
            else
            {
                statusStore.Write(NewSupervisorStatus(
                    "running",
                    process.Id,
                    consecutiveFailures,
                    lastExitCode: null,
                    nextRetryAtUtc: null,
                    $"Listening on {LoopbackEndpoint.WebSocketUrl(port)}"));
                Console.WriteLine($"Continuity backend ready (PID {process.Id}).");
                try
                {
                    await process.WaitForExitAsync(shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
            }

            await AwaitLogPumpsAsync(stdout, stderr);
            if (!shutdown.IsCancellationRequested)
            {
                var uptime = DateTimeOffset.UtcNow - startedAt;
                consecutiveFailures = uptime >= TimeSpan.FromMinutes(2)
                    ? 1
                    : consecutiveFailures + 1;
                var delay = backoff.DelayForFailure(
                    consecutiveFailures,
                    Random.Shared.NextDouble());
                var nextRetryAt = DateTimeOffset.UtcNow + delay;
                statusStore.Write(NewSupervisorStatus(
                    "backingOff",
                    backendProcessId: null,
                    consecutiveFailures,
                    process.ExitCode,
                    nextRetryAt,
                    $"App-server exited after {uptime}."));
                Console.Error.WriteLine(
                    $"App-server exited with code {process.ExitCode}; restarting in {delay.TotalSeconds:F1} seconds.");
                await Task.Delay(delay, shutdown.Token);
            }
        }

        statusStore.Write(NewSupervisorStatus(
            "stopped",
            backendProcessId: null,
            consecutiveFailures,
            lastExitCode: null,
            nextRetryAtUtc: null,
            "Supervisor stopped without changing future-launch configuration."));
        return 0;
    }

    private static SupervisorStatus NewSupervisorStatus(
        string state,
        int? backendProcessId,
        int consecutiveFailures,
        int? lastExitCode,
        DateTimeOffset? nextRetryAtUtc,
        string? detail) => new(
            state,
            Environment.ProcessId,
            backendProcessId,
            consecutiveFailures,
            lastExitCode,
            DateTimeOffset.UtcNow,
            nextRetryAtUtc,
            detail);

    private static async Task AwaitLogPumpsAsync(params Task[] pumps)
    {
        try
        {
            await Task.WhenAll(pumps);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<int> InstallAsync(
        int port,
        bool startNow,
        TrayInstallMode trayInstallMode)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            !Path.GetFileNameWithoutExtension(executable)
                .Equals("CodexContinuity", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Run install from the published CodexContinuity executable, not through dotnet run.");
        }

        var stateDirectory = ContinuityPaths.StateDirectory;
        var coordinator = CreateInstallCoordinator(stateDirectory);
        var outcome = coordinator.Install(executable, port, trayInstallMode);
        var state = outcome.State;
        Console.WriteLine(
            $"Configured future Codex desktop launches to use {state.AppServerUrl.AppliedValue}.");
        Console.WriteLine("Disabled the desktop's in-app updater for future launches.");
        Console.WriteLine("Registered the continuity supervisor to start at user logon.");
        Console.WriteLine(state.InstalledTrayExecutable is null
            ? "Left the optional notification-area controller disabled."
            : "Registered the disposable notification-area controller to start at user logon.");
        Console.WriteLine($"Staged the coordinator at {state.InstalledExecutable}.");
        Console.WriteLine("The currently running Codex desktop process was not changed or restarted.");
        if (outcome.StagedUpgrade)
        {
            Console.WriteLine(
                "The new build is staged for the next safe supervisor start; the previous build remains available for rollback.");
        }

        if (startNow)
        {
            if (await IsReadyAsync(port, TimeSpan.FromSeconds(1)))
            {
                Console.WriteLine(
                    "A ready backend already owns the configured endpoint; it was left untouched.");
            }
            else
            {
                using var process = StartSupervisor(state.InstalledExecutable, state.Port);
                if (!await WaitUntilReadyAsync(
                        state.Port,
                        process,
                        TimeSpan.FromSeconds(20),
                        CancellationToken.None))
                {
                    throw new InvalidOperationException(
                        process.HasExited
                            ? $"Continuity supervisor exited with code {process.ExitCode}."
                            : "Continuity supervisor did not become ready within 20 seconds.");
                }
                Console.WriteLine(
                    $"Started the continuity supervisor in the background (PID {process.Id}).");
            }

            if (state.InstalledTrayExecutable is not null)
            {
                using var trayProcess = StartTray(state.InstalledTrayExecutable);
                Console.WriteLine(
                    $"Started the optional notification-area controller (PID {trayProcess.Id}).");
            }
        }

        return 0;
    }

    private static int Uninstall()
    {
        var removed = CreateInstallCoordinator(ContinuityPaths.StateDirectory).Uninstall();
        Console.WriteLine(removed
            ? "Removed owned future-launch configuration. No running process was stopped."
            : "No owned future-launch configuration was found. No running process was stopped.");
        return removed ? 0 : 1;
    }

    private static int Rollback()
    {
        var state = CreateInstallCoordinator(ContinuityPaths.StateDirectory).Rollback();
        Console.WriteLine($"Staged previous build for the next safe start: {state.InstalledExecutable}");
        Console.WriteLine("The currently running supervisor and active agents were not changed.");
        return 0;
    }

    private static InstallCoordinator CreateInstallCoordinator(string stateDirectory) => new(
        stateDirectory,
        new WindowsInstallPlatform(),
        new InstallStateStore(ContinuityPaths.InstallStateFile(stateDirectory)));

    private static Process StartSupervisor(string executable, int port)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? ContinuityPaths.StateDirectory,
        };
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
    }

    private static Process StartTray(string executable)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? ContinuityPaths.StateDirectory,
        };
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
    }

    private static async Task<int> SelfTestAsync()
    {
        var codexPath = FindCodexExecutable();
        var port = FindAvailablePort();
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"codex-continuity-self-test-{Guid.NewGuid():N}");
        var codexHome = Path.Combine(testRoot, "codex-home");
        var workspace = Path.Combine(testRoot, "workspace");
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(workspace);

        Process? process = null;
        try
        {
            process = StartAppServer(codexPath, port, codexHome);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!await WaitUntilReadyAsync(port, process, TimeSpan.FromSeconds(20), CancellationToken.None))
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new InvalidOperationException(
                    $"Isolated app-server failed to start: {await stderr}");
            }

            string threadId;
            await using (var firstConnection = await RpcClient.ConnectAsync(
                             LoopbackEndpoint.WebSocketUrl(port)))
            {
                var response = await firstConnection.RequestAsync("thread/start", new JsonObject
                {
                    ["cwd"] = workspace,
                    ["approvalPolicy"] = "never",
                    ["sandbox"] = "read-only",
                });
                if (response["error"] is not null)
                {
                    throw new InvalidOperationException($"thread/start failed: {response["error"]}");
                }
                threadId = response["result"]?["thread"]?["id"]?.GetValue<string>()
                    ?? throw new InvalidOperationException(
                        $"thread/start returned no thread id: {response.ToJsonString()}");
            }

            await using (var secondConnection = await RpcClient.ConnectAsync(
                             LoopbackEndpoint.WebSocketUrl(port)))
            {
                var response = await secondConnection.RequestAsync(
                    "thread/loaded/list",
                    new JsonObject());
                if (response["error"] is not null)
                {
                    throw new InvalidOperationException(
                        $"thread/loaded/list failed: {response["error"]}");
                }
                var loadedThreadIds = response["result"]?["data"]?.AsArray()
                    .Select(node => node?.GetValue<string>())
                    .Where(id => id is not null)
                    .ToList() ?? [];
                if (!loadedThreadIds.Contains(threadId))
                {
                    throw new InvalidOperationException(
                        "The thread disappeared after the WebSocket client disconnected.");
                }
            }

            Console.WriteLine(new JsonObject
            {
                ["passed"] = true,
                ["isolated"] = true,
                ["appServerPid"] = process.Id,
                ["threadId"] = threadId,
                ["reconnected"] = true,
                ["threadPersistedAcrossReconnect"] = true,
            }.ToJsonString(JsonOptions));

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            return 0;
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            await DeleteSelfTestDirectoryAsync(testRoot);
        }
    }

    private static Process StartAppServer(string executable, int port, string? codexHome = null)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("features.code_mode_host=true");
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add(LoopbackEndpoint.WebSocketUrl(port));
        startInfo.ArgumentList.Add("--analytics-default-enabled");
        if (codexHome is not null)
        {
            startInfo.Environment["CODEX_HOME"] = codexHome;
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
    }

    private static async Task<bool> WaitUntilReadyAsync(
        int port,
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !process.HasExited && !cancellationToken.IsCancellationRequested)
        {
            if (await IsReadyAsync(port, TimeSpan.FromMilliseconds(500)))
            {
                return true;
            }
            await Task.Delay(100, cancellationToken);
        }
        return false;
    }

    private static async Task<bool> IsReadyAsync(int port, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = timeout };
        try
        {
            using var response = await client.GetAsync(LoopbackEndpoint.ReadyUrl(port));
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private static async Task PumpLogAsync(
        StreamReader reader,
        RollingLogWriter logWriter,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }
                await logWriter.AppendLineAsync(line, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string FindCodexExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CODEX_CONTINUITY_CODEX_PATH");
        if (File.Exists(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var candidates = new List<string>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var managedBin = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        if (Directory.Exists(managedBin))
        {
            candidates.AddRange(Directory.EnumerateFiles(
                managedBin,
                "codex.exe",
                SearchOption.AllDirectories));
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(Path.PathSeparator)
            .Where(Directory.Exists)
            .Select(directory => Path.Combine(directory, "codex.exe"))
            .Where(File.Exists));

        var selected = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(candidate => !candidate.Contains(
                @"\WindowsApps\OpenAI.Codex_",
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return selected ?? throw new FileNotFoundException(
            "Could not find a user-executable codex.exe. Set CODEX_CONTINUITY_CODEX_PATH.");
    }

    private static async Task<JsonObject?> ReadInstalledPackageAsync()
    {
        var script =
            "Get-AppxPackage -Name OpenAI.Codex | Select-Object Name,Version,PackageFullName | ConvertTo-Json -Compress";
        var result = await RunProcessAsync("powershell.exe", "-NoProfile", "-Command", script);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        var source = JsonNode.Parse(result.StandardOutput)?.AsObject();
        return source is null
            ? null
            : new JsonObject
            {
                ["name"] = source["Name"]?.GetValue<string>(),
                ["version"] = source["Version"]?.GetValue<string>(),
                ["packageFullName"] = source["PackageFullName"]?.GetValue<string>(),
            };
    }

    private static async Task<JsonObject?> ReadUpdateManifestAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync(UpdateManifestUrl);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject();
    }

    private static int CompareVersions(string? installed, string? available)
    {
        return Version.TryParse(installed, out var installedVersion) &&
               Version.TryParse(available, out var availableVersion)
            ? installedVersion.CompareTo(availableVersion)
            : 0;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static int FindAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task DeleteSelfTestDirectoryAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var tempPath = Path.GetFullPath(Path.GetTempPath());
        if (!fullPath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith(
                "codex-continuity-self-test-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to clean unexpected test path: {fullPath}");
        }

        for (var attempt = 0; attempt < 20 && Directory.Exists(fullPath); attempt++)
        {
            try
            {
                Directory.Delete(fullPath, recursive: true);
            }
            catch (IOException) when (attempt < 19)
            {
                await Task.Delay(100 + attempt * 25);
            }
            catch (UnauthorizedAccessException) when (attempt < 19)
            {
                await Task.Delay(100 + attempt * 25);
            }
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
    private sealed record ThreadSummary(string Id, string? Name, string Status);

    private sealed class RpcClient : IAsyncDisposable
    {
        private readonly ClientWebSocket socket = new();
        private long nextId;

        private RpcClient()
        {
        }

        public static async Task<RpcClient> ConnectAsync(string url)
        {
            var client = new RpcClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.socket.ConnectAsync(new Uri(url), timeout.Token);
            var initialize = await client.RequestAsync("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "codex_continuity",
                    ["title"] = "Codex Continuity",
                    ["version"] = "0.1.0",
                },
                ["capabilities"] = new JsonObject(),
            });
            if (initialize["error"] is not null)
            {
                throw new InvalidOperationException(
                    $"App-server initialization failed: {initialize["error"]}");
            }
            await client.SendAsync(new JsonObject
            {
                ["method"] = "initialized",
                ["params"] = new JsonObject(),
            }, timeout.Token);
            return client;
        }

        public async Task<List<ThreadSummary>> ListThreadsAsync()
        {
            var threads = new List<ThreadSummary>();
            string? cursor = null;
            do
            {
                var parameters = new JsonObject { ["limit"] = 100 };
                if (cursor is not null)
                {
                    parameters["cursor"] = cursor;
                }
                var response = await RequestAsync("thread/list", parameters);
                ThrowIfRpcError(response, "thread/list");
                var result = response["result"]?.AsObject()
                    ?? throw new InvalidOperationException("thread/list returned no result.");
                foreach (var node in result["data"]?.AsArray() ?? [])
                {
                    if (node is null)
                    {
                        continue;
                    }
                    threads.Add(new ThreadSummary(
                        node["id"]?.GetValue<string>() ?? string.Empty,
                        node["name"]?.GetValue<string>(),
                        node["status"]?["type"]?.GetValue<string>() ?? "unknown"));
                }
                cursor = result["nextCursor"]?.GetValue<string>();
            }
            while (cursor is not null);
            return threads;
        }

        public async Task<JsonObject> RequestAsync(string method, JsonObject parameters)
        {
            var id = Interlocked.Increment(ref nextId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await SendAsync(new JsonObject
            {
                ["method"] = method,
                ["id"] = id,
                ["params"] = parameters,
            }, timeout.Token);

            while (true)
            {
                var message = await ReceiveAsync(timeout.Token);
                if (message["id"]?.GetValue<long>() == id)
                {
                    return message;
                }
            }
        }

        private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }

        private async Task<JsonObject> ReceiveAsync(CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream();
            var buffer = new byte[16 * 1024];
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("App-server closed the WebSocket connection.");
                }
                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return JsonNode.Parse(Encoding.UTF8.GetString(stream.ToArray()))?.AsObject()
                ?? throw new InvalidOperationException("App-server returned invalid JSON.");
        }

        private static void ThrowIfRpcError(JsonObject response, string method)
        {
            if (response["error"] is not null)
            {
                throw new InvalidOperationException($"{method} failed: {response["error"]}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "done",
                        timeout.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (WebSocketException)
                {
                }
            }
            socket.Dispose();
        }
    }
}
