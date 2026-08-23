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
        var codexHome = FutureProcessEnvironment.ResolveCodexHome();
        Console.WriteLine(
            $"Supervising {LoopbackEndpoint.WebSocketUrl(publicPort)} with logs at {logPath}");

        var backendPort = Program.FindAvailablePort(publicPort);
        var relay = LoopbackRelay.Start(
            publicPort,
            backendPort,
            startGated: true,
            reportError: exception => Console.Error.WriteLine(
                $"Loopback relay connection failed: {exception.Message}"));
        await using var ownedRelay = relay;

        using var process = startBackend(backendPort);
        var stdout = Program.PumpLogAsync(process.StandardOutput, logWriter, shutdownToken);
        var stderr = Program.PumpLogAsync(process.StandardError, logWriter, shutdownToken);
        try
        {
            if (!await Program.WaitUntilReadyAsync(
                    backendPort,
                    process,
                    TimeSpan.FromSeconds(20),
                    shutdownToken))
            {
                return shutdownToken.IsCancellationRequested ? 0 : 1;
            }

            relay.OpenGate();
            statusStore.Write(Program.NewSupervisorStatus(
                "running",
                publicPort,
                codexHome,
                process.Id,
                consecutiveFailures: 0,
                lastExitCode: null,
                nextRetryAtUtc: null,
                $"Relaying {LoopbackEndpoint.WebSocketUrl(publicPort)} " +
                "to an owned private backend."));
            Console.WriteLine($"Continuity backend ready (PID {process.Id}).");
            try
            {
                await process.WaitForExitAsync(shutdownToken);
                return process.ExitCode;
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                return 0;
            }
        }
        finally
        {
            try
            {
                if (!relay.IsGated)
                {
                    await relay.CloseGateAsync();
                }
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    await process.WaitForExitAsync();
                }
                await Program.AwaitLogPumpsAsync(stdout, stderr);
            }
            statusStore.Write(Program.NewSupervisorStatus(
                "stopped",
                publicPort,
                codexHome,
                backendProcessId: null,
                consecutiveFailures: 0,
                lastExitCode: null,
                nextRetryAtUtc: null,
                "Supervisor stopped without changing future-launch configuration."));
        }
    }
}
