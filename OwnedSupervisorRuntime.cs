namespace CodexContinuity;

internal static class OwnedSupervisorRuntime
{
    internal static async Task<int> RunAsync(
        int publicPort,
        string stateDirectory,
        CancellationToken shutdownToken,
        Func<int, WindowsProcessGroup> startBackend)
    {
        Directory.CreateDirectory(stateDirectory);
        var logPath = ContinuityPaths.AppServerLogFile(stateDirectory);
        var logWriter = new RollingLogWriter(logPath);
        var statusStore = new SupervisorStatusStore(
            ContinuityPaths.SupervisorStatusFile(stateDirectory));
        var backoff = new RestartBackoffPolicy();
        var codexHome = FutureProcessEnvironment.ResolveCodexHome();
        var consecutiveFailures = 0;
        Console.WriteLine(
            $"Supervising {LoopbackEndpoint.WebSocketUrl(publicPort)} with logs at {logPath}");

        var backendPort = Program.FindAvailablePort(publicPort);
        LoopbackRelay relay;
        try
        {
            relay = LoopbackRelay.Start(
                publicPort,
                backendPort,
                startGated: true,
                reportError: exception => Console.Error.WriteLine(
                    $"Loopback relay connection failed: {exception.Message}"));
        }
        catch (System.Net.Sockets.SocketException)
        {
            statusStore.Write(Program.NewSupervisorStatus(
                "foreignEndpoint",
                publicPort,
                codexHome,
                backendProcessId: null,
                consecutiveFailures,
                lastExitCode: null,
                nextRetryAtUtc: null,
                "An endpoint not owned by this supervisor already uses the configured port."));
            Console.Error.WriteLine(
                "The configured loopback port is already owned by another endpoint; " +
                "refusing to adopt its thread store.");
            return 1;
        }
        await using var ownedRelay = relay;

        while (!shutdownToken.IsCancellationRequested)
        {
            relay.SetBackendPort(backendPort);
            using var process = startBackend(backendPort);
            var startedAt = DateTimeOffset.UtcNow;
            var stdout = Program.PumpLogAsync(
                process.StandardOutput,
                logWriter,
                shutdownToken);
            var stderr = Program.PumpLogAsync(
                process.StandardError,
                logWriter,
                shutdownToken);

            if (!await Program.WaitUntilReadyAsync(
                    backendPort,
                    process,
                    TimeSpan.FromSeconds(20),
                    shutdownToken))
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
                await process.WaitForExitAsync();
            }
            else
            {
                relay.OpenGate();
                statusStore.Write(Program.NewSupervisorStatus(
                    "running",
                    publicPort,
                    codexHome,
                    process.Id,
                    consecutiveFailures,
                    lastExitCode: null,
                    nextRetryAtUtc: null,
                    $"Relaying {LoopbackEndpoint.WebSocketUrl(publicPort)} " +
                    "to an owned private backend."));
                Console.WriteLine($"Continuity backend ready (PID {process.Id}).");
                try
                {
                    await process.WaitForExitAsync(shutdownToken);
                }
                catch (OperationCanceledException)
                {
                    await relay.CloseGateAsync();
                    if (!process.HasExited)
                    {
                        process.Kill();
                        await process.WaitForExitAsync();
                    }
                }
            }

            if (!relay.IsGated)
            {
                await relay.CloseGateAsync();
            }
            await Program.AwaitLogPumpsAsync(stdout, stderr);
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
                statusStore.Write(Program.NewSupervisorStatus(
                    "backingOff",
                    publicPort,
                    codexHome,
                    backendProcessId: null,
                    consecutiveFailures,
                    process.ExitCode,
                    nextRetryAt,
                    $"App-server exited after {uptime}."));
                Console.Error.WriteLine(
                    $"App-server exited with code {process.ExitCode}; " +
                    $"restarting in {delay.TotalSeconds:F1} seconds.");
                if (!await Program.WaitForRestartAsync(delay, shutdownToken))
                {
                    break;
                }
                backendPort = Program.FindAvailablePort(publicPort, backendPort);
            }
        }

        statusStore.Write(Program.NewSupervisorStatus(
            "stopped",
            publicPort,
            codexHome,
            backendProcessId: null,
            consecutiveFailures,
            lastExitCode: null,
            nextRetryAtUtc: null,
            "Supervisor stopped without changing future-launch configuration."));
        return 0;
    }
}
