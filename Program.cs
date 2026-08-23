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
    private const int StatusControlCExit = unchecked((int)0xC000013A);
    private const string UpdateManifestUrl =
        "https://persistent.oaistatic.com/codex-app-prod/windows-store-update.json";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var setupExecutable = Environment.ProcessPath is { } processPath &&
                Path.GetFileNameWithoutExtension(processPath).EndsWith(
                    "Setup",
                    StringComparison.OrdinalIgnoreCase);
            var command = ResolveCommand(setupExecutable, args);
            var port = ParsePort(args) ?? DefaultPort;
            return command switch
            {
                "help" or "--help" or "-h" => PrintHelp(),
                "probe" => await ProbeAsync(port),
                "status" => await PrintStatusAsync(port),
                "handoff-plan" => await PrintHandoffPlanAsync(port),
                "update" => await UpdateAsync(),
                "serve" => await ServeAsync(port),
                "install" => await InstallAsync(
                    port,
                    args.Contains("--start-now", StringComparer.OrdinalIgnoreCase),
                    args.Contains("--no-tray", StringComparer.OrdinalIgnoreCase)
                        ? TrayInstallMode.Disabled
                        : TrayInstallMode.Enabled,
                    ResolveInstallIntent(args),
                    ResolveAutomaticUpdateSha256(args)),
                "repair" => await RepairAsync(args),
                "uninstall" => await UninstallAsync(),
                "rollback" => Rollback(),
                "setup" when args.Contains(
                    "--uninstall",
                    StringComparer.OrdinalIgnoreCase) => await UninstallAsync(),
                "setup" => await BootstrapInstaller.RunAsync(
                    port,
                    args.Contains("--no-tray", StringComparer.OrdinalIgnoreCase)
                        ? TrayInstallMode.Disabled
                        : TrayInstallMode.Enabled,
                    startNow: !args.Contains("--no-start", StringComparer.OrdinalIgnoreCase),
                    skipSelfTest: args.Contains(
                        "--skip-self-test",
                        StringComparer.OrdinalIgnoreCase),
                    quiet: args.Contains("--silent", StringComparer.OrdinalIgnoreCase),
                    DownloadBaseUrl(args)),
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

    internal static string ResolveCommand(bool setupExecutable, string[] args)
    {
        var firstArgument = args.FirstOrDefault()?.ToLowerInvariant();
        if (firstArgument is "--help" or "-h" or "help")
        {
            return "help";
        }
        return setupExecutable &&
            (firstArgument is null || firstArgument.StartsWith("--", StringComparison.Ordinal))
                ? "setup"
                : firstArgument ?? "help";
    }

    internal static InstallIntent ResolveInstallIntent(string[] args) =>
        args.Contains("--automatic-update", StringComparer.OrdinalIgnoreCase)
            ? InstallIntent.AutomaticUpdate
            : InstallIntent.Interactive;

    internal static string? ResolveAutomaticUpdateSha256(string[] args) =>
        ArgumentValue(args, "--automatic-update-from-sha256");

    private static int PrintHelp()
    {
        Console.WriteLine(
            """
            Codex Continuity keeps the Codex app-server alive independently of the desktop UI.

            Commands:
              probe       Inspect the installed desktop, update manifest, and backend configuration.
              status      Check backend health and count active threads.
              handoff-plan  Read whether lifecycle work must wait, apply an update, or may hand off.
              update      Check for and safely stage a verified Continuity release.
              serve       Supervise a loopback WebSocket app-server.
              install     Configure future desktop launches and start at user logon.
              repair      Reapply the persisted port and tray choices without restarting agents.
              uninstall   Remove owned configuration and schedule installed files for cleanup.
              rollback    Stage the previous known-good build for the next safe start.
              setup       Download, verify, self-test, and install the matching release bundle.
              self-test   Prove reconnect and persisted-thread behavior in an isolated Codex home.

            Options:
              --port N       Loopback port (default: 45123).
              --start-now    With install, start the supervisor without touching the desktop app.
              --no-tray      With install, omit the disposable notification-area controller.
              --no-start     With setup, configure startup without launching the supervisor now.
              --silent       With setup, suppress progress output for unattended installation.
              --skip-self-test  With setup, omit the isolated reconnect proof.
              --uninstall   With setup, uninstall without stopping agents; files leave next sign-in.

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

    private static string? DownloadBaseUrl(string[] args)
        => ArgumentValue(args, "--download-base-url");

    private static string? ArgumentValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{name} requires a value.");
            }
            return args[index + 1];
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
        var installState = LoadInstallState();

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
                LoadSupervisorStatus(),
                JsonOptions),
        };
        Console.WriteLine(result.ToJsonString(JsonOptions));
        return 0;
    }

    private static Task<int> PrintHandoffPlanAsync(int port) => PrintHandoffPlanAsync(
        ContinuityPaths.StateDirectory,
        () => ObserveThreadLifecyclesAsync(port),
        Console.Out);

    internal static async Task<int> PrintHandoffPlanAsync(
        string stateDirectory,
        Func<Task<ContinuityThreadSnapshot>> observeThreads,
        TextWriter output)
    {
        var snapshot = await observeThreads();
        var updateState = new ContinuityUpdateStateStore(
            ContinuityPaths.UpdateStatusFile(stateDirectory)).Load();
        var selectedBuild = ContinuitySelectedBuildReader.Load(stateDirectory);
        var plan = ContinuityHandoffPlanner.Create(
            snapshot.BackendReady,
            snapshot.Threads,
            updateState,
            selectedBuild);
        await output.WriteLineAsync(JsonSerializer.Serialize(plan, HandoffJsonOptions));
        return 0;
    }

    private static async Task<ContinuityThreadSnapshot> ObserveThreadLifecyclesAsync(int port)
    {
        var status = LoadSupervisorStatus();
        if (status is null ||
            !await IsManagedEndpointReadyAsync(port, status.SupervisorProcessId))
        {
            return new(BackendReady: false, Threads: []);
        }

        try
        {
            await using var client = await RpcClient.ConnectAsync(
                LoopbackEndpoint.WebSocketUrl(port));
            var threads = (await client.ListThreadsAsync()).Select(
                thread => thread.Activity).ToList();
            return Equals(status, LoadSupervisorStatus()) &&
                await IsManagedEndpointReadyAsync(port, status.SupervisorProcessId)
                    ? new(BackendReady: true, threads)
                    : new(BackendReady: false, Threads: []);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or InvalidOperationException or
                OperationCanceledException or WebSocketException)
        {
            return new(BackendReady: false, Threads: []);
        }
    }

    private static Task<int> ServeAsync(int port) =>
        ServeAsync(port, ContinuityPaths.StateDirectory);

    internal static Task<int> ServeAsync(int port, string stateDirectory) =>
        ServeAsync(port, stateDirectory, AutomaticUpdateRunner.RunAsync);

    internal static async Task<int> ServeAsync(
        int port,
        string stateDirectory,
        Func<string, string, CancellationToken, Task> runUpdates)
    {
        using var mutex = new Mutex(
            initiallyOwned: true,
            $"Local\\CodexContinuity-{port}",
            out var ownsMutex);
        if (!ownsMutex)
        {
            return Fail($"A Codex Continuity supervisor already owns port {port}.");
        }

        Directory.CreateDirectory(stateDirectory);
        var logPath = ContinuityPaths.AppServerLogFile(stateDirectory);
        var logWriter = new RollingLogWriter(logPath);
        var statusStore = new SupervisorStatusStore(
            ContinuityPaths.SupervisorStatusFile(stateDirectory));
        var backoff = new RestartBackoffPolicy();
        var codexHome = FutureProcessEnvironment.ResolveCodexHome();
        var consecutiveFailures = 0;
        await using var updateLifetime = new SupervisorUpdateLifetime(
            stateDirectory,
            ProductVersion(),
            runUpdates);
        var shutdownToken = updateLifetime.Token;
        Console.WriteLine(
            $"Supervising {LoopbackEndpoint.WebSocketUrl(port)} with logs at {logPath}");

        while (!shutdownToken.IsCancellationRequested)
        {
            if (await IsReadyAsync(port, TimeSpan.FromMilliseconds(500)))
            {
                statusStore.Write(NewSupervisorStatus(
                    "foreignEndpoint",
                    port,
                    codexHome,
                    backendProcessId: null,
                    consecutiveFailures,
                    lastExitCode: null,
                    nextRetryAtUtc: null,
                    "An endpoint not owned by this supervisor already uses the configured port."));
                return Fail(
                    "The configured loopback port is already owned by another endpoint; refusing to adopt its thread store.");
            }

            var codexPath = FindCodexExecutable(persistedEnvironmentOnly: true);
            using var process = StartAppServer(codexPath, port);
            var startedAt = DateTimeOffset.UtcNow;
            var stdout = PumpLogAsync(process.StandardOutput, logWriter, shutdownToken);
            var stderr = PumpLogAsync(process.StandardError, logWriter, shutdownToken);

            if (!await WaitUntilReadyAsync(port, process, TimeSpan.FromSeconds(20), shutdownToken))
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
                await process.WaitForExitAsync();
            }
            else
            {
                statusStore.Write(NewSupervisorStatus(
                    "running",
                    port,
                    codexHome,
                    process.Id,
                    consecutiveFailures,
                    lastExitCode: null,
                    nextRetryAtUtc: null,
                    $"Listening on {LoopbackEndpoint.WebSocketUrl(port)}"));
                Console.WriteLine($"Continuity backend ready (PID {process.Id}).");
                try
                {
                    await process.WaitForExitAsync(shutdownToken);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        await process.WaitForExitAsync();
                    }
                }
            }

            await AwaitLogPumpsAsync(stdout, stderr);
            if (!shutdownToken.IsCancellationRequested)
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
                    port,
                    codexHome,
                    backendProcessId: null,
                    consecutiveFailures,
                    process.ExitCode,
                    nextRetryAt,
                    $"App-server exited after {uptime}."));
                Console.Error.WriteLine(
                    $"App-server exited with code {process.ExitCode}; restarting in {delay.TotalSeconds:F1} seconds.");
                if (!await WaitForRestartAsync(delay, shutdownToken))
                {
                    break;
                }
            }
        }

        statusStore.Write(NewSupervisorStatus(
            "stopped",
            port,
            codexHome,
            backendProcessId: null,
            consecutiveFailures,
            lastExitCode: null,
            nextRetryAtUtc: null,
            "Supervisor stopped without changing future-launch configuration."));
        return 0;
    }

    internal static async Task<bool> WaitForRestartAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task<int> UpdateAsync()
    {
        var result = await AutomaticUpdateRunner.CheckOnceAsync(
            ContinuityPaths.StateDirectory,
            runningVersion: null,
            CancellationToken.None);
        if (result.Kind == AutomaticUpdateCheckKind.NotInstalled)
        {
            return Fail("No installed Continuity state is available for automatic updates.");
        }
        if (result.Kind == AutomaticUpdateCheckKind.DeferredUninstall)
        {
            return Fail("Continuity is pending deferred uninstall; automatic updates are disabled.");
        }
        if (result.Kind == AutomaticUpdateCheckKind.Busy)
        {
            return Fail("Another automatic update check is already in progress.");
        }
        Console.WriteLine(JsonSerializer.Serialize(result.State, JsonOptions));
        return result.State?.LastError is null ? 0 : 1;
    }

    private static SupervisorStatus NewSupervisorStatus(
        string state,
        int port,
        string codexHome,
        int? backendProcessId,
        int consecutiveFailures,
        int? lastExitCode,
        DateTimeOffset? nextRetryAtUtc,
        string? detail) => new(
            state,
            Environment.ProcessId,
            backendProcessId,
            port,
            codexHome,
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
        TrayInstallMode trayInstallMode,
        InstallIntent intent = InstallIntent.Interactive,
        string? expectedInstalledSha256 = null,
        LifecycleLockOwnership lockOwnership = LifecycleLockOwnership.Acquire)
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
        using var lifecycleLock = lockOwnership == LifecycleLockOwnership.Acquire
            ? ContinuityLifecycleLock.Acquire(stateDirectory)
            : null;
        var existingState = LoadInstallState();
        await EnsurePortChangeIsSafeAsync(
            existingState?.Port,
            port,
            installedPort => IsReadyAsync(installedPort, TimeSpan.FromSeconds(1)));
        var coordinator = CreateInstallCoordinator(stateDirectory);
        var endpointOwnership = ExistingEndpointOwnership.NotReady;
        if (startNow && await IsReadyAsync(port, TimeSpan.FromSeconds(1)))
        {
            endpointOwnership = await IsManagedEndpointReadyAsync(port)
                ? ExistingEndpointOwnership.Managed
                : coordinator.DetectLegacyInstalledPort() == port
                    ? ExistingEndpointOwnership.Legacy
                    : ExistingEndpointOwnership.Foreign;
        }
        var outcome = coordinator.Install(
            executable,
            port,
            trayInstallMode,
            endpointOwnership,
            intent,
            expectedInstalledSha256,
            LifecycleLockOwnership.AlreadyHeld);
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
            if ((endpointOwnership is ExistingEndpointOwnership.Managed or
                    ExistingEndpointOwnership.Legacy) &&
                await IsReadyAsync(port, TimeSpan.FromSeconds(1)))
            {
                Console.WriteLine(endpointOwnership == ExistingEndpointOwnership.Managed
                    ? "The running Continuity backend already owns the configured endpoint; it was left untouched."
                    : "The running previous Continuity backend was left untouched; the new build is staged for the next safe start.");
            }
            else
            {
                using var process = StartSupervisor(state.InstalledExecutable, state.Port);
                if (!await WaitUntilManagedSupervisorReadyAsync(
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

    internal static async Task EnsurePortChangeIsSafeAsync(
        int? installedPort,
        int requestedPort,
        Func<int, Task<bool>> isReadyAsync)
    {
        if (installedPort is null || installedPort == requestedPort)
        {
            return;
        }

        if (await isReadyAsync(installedPort.Value))
        {
            throw new InvalidOperationException(
                $"The installed Continuity backend on port {installedPort} is still ready. " +
                $"Refusing to redirect future Codex launches to port {requestedPort} while it may own active work. " +
                "Let that work finish, stop the old supervisor, and then retry the port change.");
        }
    }

    private static async Task<int> UninstallAsync()
    {
        using var lifecycleLock = ContinuityLifecycleLock.Acquire(
            ContinuityPaths.StateDirectory);
        var coordinator = CreateInstallCoordinator(ContinuityPaths.StateDirectory);
        var installState = LoadInstallState();
        var legacyInstalledPort = installState is null
            ? coordinator.DetectLegacyInstalledPort()
            : null;
        var configuredUrl = Environment.GetEnvironmentVariable(
            InstallCoordinator.AppServerUrlVariable,
            EnvironmentVariableTarget.User);
        var reconnectPolicy = await ResolveUninstallReconnectPolicyAsync(
            installState?.Port,
            legacyInstalledPort,
            configuredUrl,
            port => IsManagedEndpointReadyAsync(port),
            port => IsReadyAsync(port, TimeSpan.FromSeconds(1)));
        var removed = coordinator.Uninstall(
            reconnectPolicy,
            LifecycleLockOwnership.AlreadyHeld);
        if (!removed)
        {
            Console.WriteLine(
                "No owned future-launch configuration was found. No running process was stopped.");
            return 1;
        }

        Console.WriteLine(
            reconnectPolicy == UninstallReconnectPolicy.PreserveUntilNextSignIn
                ? "Removed future startup configuration. Codex reopenings in this Windows session will keep reconnecting to the running backend; the owned reconnect setting and installed files will be removed at the next sign-in. No running process was stopped."
                : "Removed owned future-launch configuration. Installed files will be removed at the next sign-in; no running process was stopped.");
        return 0;
    }

    internal static async Task<UninstallReconnectPolicy> ResolveUninstallReconnectPolicyAsync(
        int? managedInstalledPort,
        int? legacyInstalledPort,
        string? configuredUrl,
        Func<int, Task<bool>> isManagedEndpointReadyAsync,
        Func<int, Task<bool>> isLegacyEndpointReadyAsync)
    {
        var installedPort = managedInstalledPort ?? legacyInstalledPort;
        if (installedPort is not { } port || !string.Equals(
                configuredUrl,
                LoopbackEndpoint.WebSocketUrl(port),
                StringComparison.Ordinal))
        {
            return UninstallReconnectPolicy.RestoreImmediately;
        }

        var endpointIsOwned = managedInstalledPort is not null
            ? await isManagedEndpointReadyAsync(port)
            : await isLegacyEndpointReadyAsync(port);
        return endpointIsOwned
            ? UninstallReconnectPolicy.PreserveUntilNextSignIn
            : UninstallReconnectPolicy.RestoreImmediately;
    }

    private static Task<int> RepairAsync(string[] args) => RepairAsync(
        args,
        ContinuityPaths.StateDirectory,
        LoadInstallState,
        (state, startNow) => InstallAsync(
            state.Port,
            startNow,
            state.InstalledTrayExecutable is null
                ? TrayInstallMode.Disabled
                : TrayInstallMode.Enabled,
            lockOwnership: LifecycleLockOwnership.AlreadyHeld));

    internal static async Task<int> RepairAsync(
        string[] args,
        string stateDirectory,
        Func<InstallState?> loadInstallState,
        Func<InstallState, bool, Task<int>> installAsync)
    {
        var startNow = args.Contains("--start-now", StringComparer.OrdinalIgnoreCase);
        var expectedInstalledExecutable = ArgumentValue(
            args,
            "--expected-installed-executable");
        var expectedInstalledSha256 = ArgumentValue(
            args,
            "--expected-installed-sha256");
        using var lifecycleLock = ContinuityLifecycleLock.Acquire(stateDirectory);
        var state = loadInstallState()
            ?? throw new InvalidOperationException("No installed Continuity state is available.");
        ValidateTrayRepairSelection(
            state,
            expectedInstalledExecutable,
            expectedInstalledSha256);
        return await installAsync(state, startNow);
    }

    internal static void ValidateTrayRepairSelection(
        InstallState state,
        string? expectedInstalledExecutable,
        string? expectedInstalledSha256)
    {
        if (expectedInstalledExecutable is null && expectedInstalledSha256 is null)
        {
            return;
        }
        if (expectedInstalledExecutable is null || expectedInstalledSha256 is null)
        {
            throw new InvalidOperationException("Tray repair intent is incomplete.");
        }
        if (state.Lifecycle != InstallLifecycle.Installed)
        {
            throw new InvalidOperationException(
                "Continuity is not in an installed lifecycle and cannot be repaired from the tray.");
        }
        if (!Path.GetFullPath(state.InstalledExecutable).Equals(
                Path.GetFullPath(expectedInstalledExecutable),
                StringComparison.OrdinalIgnoreCase) ||
            !state.BinarySha256.Equals(expectedInstalledSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The selected Continuity build changed before tray recovery acquired the lifecycle lock.");
        }
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
        new InstallStateStore(ContinuityPaths.InstallStateFile(stateDirectory)),
        ContinuityPaths.LegacyOpenAiStateDirectory);

    private static InstallState? LoadInstallState() =>
        new InstallStateStore(
            ContinuityPaths.InstallStateFile(ContinuityPaths.StateDirectory)).Load() ??
        new InstallStateStore(
            ContinuityPaths.InstallStateFile(ContinuityPaths.LegacyOpenAiStateDirectory)).Load();

    private static SupervisorStatus? LoadSupervisorStatus() =>
        new SupervisorStatusStore(
            ContinuityPaths.SupervisorStatusFile(ContinuityPaths.StateDirectory)).Read() ??
        new SupervisorStatusStore(
            ContinuityPaths.SupervisorStatusFile(
                ContinuityPaths.LegacyOpenAiStateDirectory)).Read();

    private static Process StartSupervisor(string executable, int port)
        => DetachedProcessLauncher.Start(
            executable,
            ["serve", "--port", port.ToString()],
            Path.GetDirectoryName(executable) ?? ContinuityPaths.StateDirectory);

    private static Process StartTray(string executable)
        => DetachedProcessLauncher.Start(
            executable,
            [],
            Path.GetDirectoryName(executable) ?? ContinuityPaths.StateDirectory);

    private static async Task<int> SelfTestAsync()
    {
        var codexPath = FindCodexExecutable();
        var result = await RunSelfTestAsync(
            startBackend: (port, codexHome) => StartAppServer(codexPath, port, codexHome),
            boundedStopTimeout: TimeSpan.FromSeconds(10));
        Console.WriteLine(result.ToJsonString(JsonOptions));
        return 0;
    }

    internal static async Task<JsonObject> RunSelfTestAsync(
        Func<int, string, WindowsProcessGroup> startBackend,
        TimeSpan boundedStopTimeout)
    {
        var publicPort = FindAvailablePort();
        var backendPort = FindAvailablePort(publicPort);
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"codex-continuity-self-test-{Guid.NewGuid():N}");
        var codexHome = Path.Combine(testRoot, "codex-home");
        var workspace = Path.Combine(testRoot, "workspace");
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(workspace);

        WindowsProcessGroup? process = null;
        LoopbackRelay? relay = null;
        try
        {
            relay = LoopbackRelay.Start(
                publicPort,
                backendPort,
                startGated: true);
            process = startBackend(backendPort, codexHome);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!await WaitUntilReadyAsync(
                    backendPort,
                    process,
                    TimeSpan.FromSeconds(20),
                    CancellationToken.None))
            {
                process.Kill();
                await process.WaitForExitAsync();
                throw new InvalidOperationException(
                    $"Isolated app-server failed to start: {await stderr}");
            }
            relay.OpenGate();

            string threadId;
            await using (var firstConnection = await RpcClient.ConnectAsync(
                             LoopbackEndpoint.WebSocketUrl(publicPort)))
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
                             LoopbackEndpoint.WebSocketUrl(publicPort)))
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

            var result = new JsonObject
            {
                ["passed"] = true,
                ["isolated"] = true,
                ["appServerPid"] = process.Id,
                ["threadId"] = threadId,
                ["relayed"] = true,
                ["reconnected"] = true,
                ["threadPersistedAcrossReconnect"] = true,
            };
            await relay.CloseGateAsync();
            var stopDisposition = await StopAppServerWithCtrlBreakAsync(
                process,
                boundedStopTimeout);
            if (stopDisposition is not (
                    AppServerStopDisposition.CleanExit or
                    AppServerStopDisposition.WindowsControlExit))
            {
                throw new InvalidOperationException(
                    "The isolated app-server did not honor bounded Ctrl+Break shutdown.");
            }
            await Task.WhenAll(stdout, stderr);
            result["boundedStop"] = true;
            result["stopDisposition"] =
                stopDisposition == AppServerStopDisposition.CleanExit
                    ? "cleanExit"
                    : "windowsControlExit";
            return result;
        }
        finally
        {
            try
            {
                await CleanupSelfTestAsync(relay, process, CancellationToken.None);
            }
            finally
            {
                process?.Dispose();
                if (relay is not null)
                {
                    await relay.DisposeAsync();
                }
                await DeleteSelfTestDirectoryAsync(testRoot);
            }
        }
    }

    internal static async Task CleanupSelfTestAsync(
        LoopbackRelay? relay,
        WindowsProcessGroup? process,
        CancellationToken cancellationToken)
    {
        try
        {
            if (relay is { IsGated: false })
            {
                await relay.CloseGateAsync(cancellationToken);
            }
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
        }
    }

    internal static async Task<AppServerStopDisposition> StopAppServerWithCtrlBreakAsync(
        WindowsProcessGroup process,
        TimeSpan timeout)
    {
        if (process.HasExited)
        {
            return AppServerStopDisposition.AlreadyExited;
        }
        if (!process.SendCtrlBreak())
        {
            return AppServerStopDisposition.AlreadyExited;
        }
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token);
            return process.ExitCode switch
            {
                0 => AppServerStopDisposition.CleanExit,
                StatusControlCExit => AppServerStopDisposition.WindowsControlExit,
                _ => AppServerStopDisposition.UnexpectedExit,
            };
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            return AppServerStopDisposition.TimedOut;
        }
    }

    private static WindowsProcessGroup StartAppServer(
        string executable,
        int port,
        string? codexHome = null)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
        };
        FutureProcessEnvironment.ApplyTo(startInfo);
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

        return WindowsProcessGroup.Start(startInfo);
    }

    private static async Task<bool> WaitUntilReadyAsync(
        int port,
        WindowsProcessGroup process,
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
            try
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
        return false;
    }

    private static async Task<bool> WaitUntilManagedSupervisorReadyAsync(
        int port,
        Process supervisor,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline &&
               !supervisor.HasExited &&
               !cancellationToken.IsCancellationRequested)
        {
            if (await IsManagedEndpointReadyAsync(port, supervisor.Id))
            {
                return true;
            }
            await Task.Delay(100, cancellationToken);
        }
        return false;
    }

    private static async Task<bool> IsManagedEndpointReadyAsync(
        int port,
        int? expectedSupervisorProcessId = null)
    {
        if (!await IsReadyAsync(port, TimeSpan.FromMilliseconds(500)))
        {
            return false;
        }

        var status = LoadSupervisorStatus();
        if (status is null ||
            status.State != "running" ||
            status.Port != port ||
            status.BackendProcessId is null ||
            (expectedSupervisorProcessId is not null &&
             status.SupervisorProcessId != expectedSupervisorProcessId) ||
            !ProcessIsRunning(status.SupervisorProcessId) ||
            !ProcessIsRunning(status.BackendProcessId.Value))
        {
            return false;
        }

        try
        {
            return status.CodexHome is not null && Path.GetFullPath(status.CodexHome).Equals(
                Path.GetFullPath(FutureProcessEnvironment.ResolveCodexHome()),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
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
        catch (InvalidOperationException)
        {
            return false;
        }
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

    private static string FindCodexExecutable(bool persistedEnvironmentOnly = false)
    {
        var persistedEnvironment = persistedEnvironmentOnly
            ? FutureProcessEnvironment.Snapshot()
            : null;
        var explicitPath = persistedEnvironment is null
            ? Environment.GetEnvironmentVariable("CODEX_CONTINUITY_CODEX_PATH")
            : persistedEnvironment.GetValueOrDefault("CODEX_CONTINUITY_CODEX_PATH");
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

        var path = persistedEnvironment is null
            ? Environment.GetEnvironmentVariable("PATH") ?? string.Empty
            : persistedEnvironment.GetValueOrDefault("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(Path.PathSeparator)
            .Where(Directory.Exists)
            .Select(directory => Path.Combine(directory, "codex.exe"))
            .Where(File.Exists));

        var selected = SelectCodexExecutable(candidates);
        return selected ?? throw new FileNotFoundException(
            "Could not find a user-executable codex.exe. Set CODEX_CONTINUITY_CODEX_PATH.");
    }

    internal static string? SelectCodexExecutable(IEnumerable<string> candidates) =>
        candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(candidate => !candidate.Contains(
                @"\WindowsApps\OpenAI.Codex_",
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

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

    private static int FindAvailablePort(params int[] excludedPorts)
    {
        while (true)
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (!excludedPorts.Contains(port))
            {
                return port;
            }
        }
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

    private static string ProductVersion()
    {
        var version = typeof(Program).Assembly.GetName().Version;
        return version is null
            ? "development"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions HandoffJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
    internal enum AppServerStopDisposition
    {
        CleanExit,
        WindowsControlExit,
        AlreadyExited,
        TimedOut,
        UnexpectedExit,
    }
    internal sealed record ThreadSummary(
        string Id,
        string? Name,
        string Status,
        ThreadLifecycleStatus Activity);

    internal sealed class RpcClient : IAsyncDisposable
    {
        private readonly ClientWebSocket socket = new();
        private long nextId;

        private RpcClient()
        {
        }

        public static async Task<RpcClient> ConnectAsync(
            string url,
            int maximumResponseBytes = RpcReadBudget.DefaultMaximumMessageBytes)
        {
            var client = new RpcClient();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.socket.ConnectAsync(new Uri(url), timeout.Token);
                var initialize = await client.RequestAsync(
                    "initialize",
                    new JsonObject
                    {
                        ["clientInfo"] = new JsonObject
                        {
                            ["name"] = "codex_continuity",
                            ["title"] = "Codex Continuity",
                            ["version"] = ProductVersion(),
                        },
                        ["capabilities"] = new JsonObject(),
                    },
                    timeout.Token,
                    maximumResponseBytes);
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
            catch
            {
                client.socket.Dispose();
                throw;
            }
        }

        public async Task<List<ThreadSummary>> ListThreadsAsync(
            int maximumThreads = RpcReadBudget.DefaultMaximumItems,
            int maximumPages = RpcReadBudget.DefaultMaximumPages,
            int maximumMessageBytes = RpcReadBudget.DefaultMaximumMessageBytes,
            TimeSpan? operationTimeout = null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumMessageBytes, 1);
            using var timeout = new CancellationTokenSource(
                operationTimeout ?? RpcReadBudget.DefaultOperationTimeout);
            var budget = new RpcReadBudget(maximumThreads, maximumPages);
            var threads = new List<ThreadSummary>();
            string? cursor = null;
            do
            {
                budget.BeginPage();
                var parameters = new JsonObject { ["limit"] = 100 };
                if (cursor is not null)
                {
                    parameters["cursor"] = cursor;
                }
                var response = await RequestAsync(
                    "thread/list",
                    parameters,
                    timeout.Token,
                    maximumMessageBytes);
                ThrowIfRpcError(response, "thread/list");
                var result = response["result"]?.AsObject()
                    ?? throw new InvalidOperationException("thread/list returned no result.");
                var page = ParseThreadData(result["data"]);
                budget.AddItems(page.Count);
                threads.AddRange(page);
                cursor = result["nextCursor"]?.GetValue<string>();
                budget.ObserveCursor(cursor);
            }
            while (cursor is not null);
            return threads;
        }

        internal static IReadOnlyList<ThreadSummary> ParseThreadData(JsonNode? dataNode)
        {
            if (dataNode is not JsonArray data)
            {
                throw new InvalidOperationException("thread/list returned no data array.");
            }

            var threads = new List<ThreadSummary>(data.Count);
            foreach (var node in data)
            {
                if (node is not JsonObject thread)
                {
                    throw new InvalidOperationException(
                        "thread/list returned a malformed thread entry.");
                }
                var activity = ThreadLifecycleStatus.Parse(thread["status"]);
                threads.Add(new ThreadSummary(
                    thread["id"]?.GetValue<string>() ?? string.Empty,
                    thread["name"]?.GetValue<string>(),
                    activity.Type,
                    activity));
            }
            return threads;
        }

        public async Task<JsonObject> RequestAsync(
            string method,
            JsonObject parameters,
            CancellationToken cancellationToken = default,
            int maximumResponseBytes = RpcReadBudget.DefaultMaximumMessageBytes)
        {
            var id = Interlocked.Increment(ref nextId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await SendAsync(new JsonObject
            {
                ["method"] = method,
                ["id"] = id,
                ["params"] = parameters,
            }, timeout.Token);

            while (true)
            {
                var message = await ReceiveAsync(timeout.Token, maximumResponseBytes);
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

        private async Task<JsonObject> ReceiveAsync(
            CancellationToken cancellationToken,
            int maximumResponseBytes)
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
                RpcReadBudget.EnsureMessageFits(
                    stream.Length,
                    result.Count,
                    maximumResponseBytes);
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
