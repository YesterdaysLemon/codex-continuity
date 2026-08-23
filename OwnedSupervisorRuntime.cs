using System.Net.Sockets;

namespace CodexContinuity;

internal sealed record BackendOwnershipChecks(
    Func<int, int, bool> IsListenerOwnedBy,
    Func<TcpClient, int, bool> IsConnectionAcceptedBy)
{
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    internal static BackendOwnershipChecks Native { get; } = new(
        WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy,
        WindowsTcpPortOwnership.IsLoopbackConnectionAcceptedBy);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(IsListenerOwnedBy);
        ArgumentNullException.ThrowIfNull(IsConnectionAcceptedBy);
        if (PollInterval <= TimeSpan.Zero || PollInterval > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        }
    }
}

internal static class OwnedSupervisorRuntime
{
    internal static async Task<int> RunAsync(
        int publicPort,
        string stateDirectory,
        CancellationToken shutdownToken,
        Func<int, WindowsProcessGroup> startBackend,
        Func<int, TimeSpan>? delayForFailure = null,
        Func<TimeSpan, CancellationToken, Task<bool>>? waitForRestart = null,
        TimeSpan? readinessTimeout = null,
        BackendOwnershipChecks? ownershipChecks = null)
    {
        Directory.CreateDirectory(stateDirectory);
        var logPath = ContinuityPaths.AppServerLogFile(stateDirectory);
        var logWriter = new RollingLogWriter(logPath);
        var statusStore = new SupervisorStatusStore(
            ContinuityPaths.SupervisorStatusFile(stateDirectory));
        var leaseStore = new BackendLeaseStore(
            ContinuityPaths.BackendLeaseFile(stateDirectory));
        var backoff = new RestartBackoffPolicy();
        delayForFailure ??= failure => backoff.DelayForFailure(
            failure,
            Random.Shared.NextDouble());
        waitForRestart ??= Program.WaitForRestartAsync;
        ownershipChecks ??= BackendOwnershipChecks.Native;
        ownershipChecks.Validate();
        var effectiveReadinessTimeout = readinessTimeout ?? TimeSpan.FromSeconds(20);
        if (effectiveReadinessTimeout <= TimeSpan.Zero ||
            effectiveReadinessTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(readinessTimeout));
        }
        var codexHome = FutureProcessEnvironment.ResolveCodexHome();
        var consecutiveFailures = 0;
        Console.WriteLine(
            $"Supervising {LoopbackEndpoint.WebSocketUrl(publicPort)} with logs at {logPath}");

        var recovery = BackendLeaseRecovery.TryRecover(
            leaseStore,
            publicPort,
            codexHome);
        if (recovery.Kind == BackendRecoveryKind.Unsafe)
        {
            statusStore.Write(Program.NewSupervisorStatus(
                "unsafeBackendLease",
                publicPort,
                codexHome,
                recovery.Lease?.BackendProcessId,
                consecutiveFailures,
                lastExitCode: null,
                nextRetryAtUtc: null,
                recovery.Detail));
            Console.Error.WriteLine(
                $"Backend ownership could not be recovered safely: {recovery.Detail}");
            return 1;
        }
        if (recovery.Kind == BackendRecoveryKind.Stale)
        {
            leaseStore.Delete();
        }

        var recoveredBackend = recovery.Backend;
        var backendPort = recovery.Kind == BackendRecoveryKind.Recovered
            ? recovery.Lease!.BackendPort
            : Program.FindAvailablePort(publicPort);
        var activeBackendProcessId = 0;
        LoopbackRelay relay;
        try
        {
            relay = LoopbackRelay.Start(
                publicPort,
                backendPort,
                startGated: true,
                reportError: exception => Console.Error.WriteLine(
                    $"Loopback relay connection failed: {exception.Message}"),
                backendAdmission: (candidatePort, connectedBackend) =>
                {
                    var processId = Volatile.Read(ref activeBackendProcessId);
                    return processId > 0 && (connectedBackend is null
                        ? ownershipChecks.IsListenerOwnedBy(candidatePort, processId)
                        : ownershipChecks.IsConnectionAcceptedBy(connectedBackend, processId));
                });
        }
        catch (System.Net.Sockets.SocketException exception) when (
            exception.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
        {
            recoveredBackend?.Dispose();
            statusStore.Write(Program.NewSupervisorStatus(
                "foreignEndpoint",
                publicPort,
                codexHome,
                backendProcessId: null,
                consecutiveFailures: 0,
                lastExitCode: null,
                nextRetryAtUtc: null,
                "An endpoint not owned by this supervisor already uses the configured port."));
            Console.Error.WriteLine(
                "The configured loopback port is already owned by another endpoint; " +
                "refusing to adopt its thread store.");
            return 1;
        }
        catch
        {
            recoveredBackend?.Dispose();
            throw;
        }
        await using var ownedRelay = relay;

        var publishStopped = true;
        try
        {
            while (!shutdownToken.IsCancellationRequested)
            {
                relay.SetBackendPort(backendPort);
                var recovered = recoveredBackend is not null;
                using var process = recoveredBackend ?? startBackend(backendPort);
                recoveredBackend = null;
                var startedAt = DateTimeOffset.UtcNow;
                var stdout = Task.CompletedTask;
                var stderr = Task.CompletedTask;
                var leaseActive = recovered;
                var preserveBackend = false;
                var lifecycleCompleted = false;
                var ownershipDefinitivelyLost = false;
                Volatile.Write(ref activeBackendProcessId, process.Id);
                try
                {
                    var backendExecutable = recovered
                        ? recovery.Lease!.BackendExecutable
                        : process.ExecutablePath;
                    var backendStartedAtUtc = recovered
                        ? recovery.Lease!.BackendStartedAtUtc
                        : process.StartedAtUtc;
                    stdout = Program.PumpLogAsync(
                        process.StandardOutput,
                        logWriter,
                        shutdownToken);
                    stderr = Program.PumpLogAsync(
                        process.StandardError,
                        logWriter,
                        shutdownToken);
                    var ready = await Program.WaitUntilReadyAsync(
                        backendPort,
                        process,
                        effectiveReadinessTimeout,
                        shutdownToken);
                    if (ready && !process.HasExited)
                    {
                        var ownership = InspectBackendOwnership(
                            backendPort,
                            process.Id,
                            ownershipChecks.IsListenerOwnedBy);
                        if (ownership == BackendOwnership.Lost && !process.HasExited)
                        {
                            await Task.WhenAny(
                                process.WaitForExitAsync(),
                                Task.Delay(TimeSpan.FromMilliseconds(100), shutdownToken));
                        }
                        if (ownership != BackendOwnership.Owned &&
                            !process.HasExited &&
                            !shutdownToken.IsCancellationRequested)
                        {
                            preserveBackend = recovered && ownership == BackendOwnership.Unknown;
                            publishStopped = false;
                            var ownershipLost = ownership == BackendOwnership.Lost;
                            ownershipDefinitivelyLost = ownershipLost;
                            var detail = ownershipLost
                                ? "The private listener is not owned by the supervised backend."
                                : recovered
                                    ? "Private listener ownership could not be inspected; " +
                                        "preserving the recovered backend lease."
                                    : "Private listener ownership could not be inspected; " +
                                        "refusing to publish the new backend.";
                            statusStore.Write(Program.NewSupervisorStatus(
                                ownershipLost
                                    ? "backendOwnershipLost"
                                    : "backendOwnershipUnknown",
                                publicPort,
                                codexHome,
                                process.Id,
                                consecutiveFailures,
                                lastExitCode: null,
                                nextRetryAtUtc: null,
                                detail));
                            Console.Error.WriteLine(detail);
                            return 1;
                        }
                        if (!process.HasExited && !shutdownToken.IsCancellationRequested)
                        {
                            leaseStore.Write(new BackendLease(
                                BackendLease.CurrentSchemaVersion,
                                OwnerSupervisorProcessId: Environment.ProcessId,
                                BackendProcessId: process.Id,
                                PublicPort: publicPort,
                                BackendPort: backendPort,
                                BackendExecutable: backendExecutable,
                                CodexHome: codexHome,
                                BackendStartedAtUtc: backendStartedAtUtc));
                            leaseActive = true;
                            relay.OpenGate();
                            statusStore.Write(Program.NewSupervisorStatus(
                                "running",
                                publicPort,
                                codexHome,
                                process.Id,
                                consecutiveFailures,
                                lastExitCode: null,
                                nextRetryAtUtc: null,
                                recovered
                                    ? "Recovered the verified private backend behind the stable endpoint."
                                    : $"Relaying {LoopbackEndpoint.WebSocketUrl(publicPort)} " +
                                        "to an owned private backend."));
                            Console.WriteLine($"Continuity backend ready (PID {process.Id}).");
                            try
                            {
                                var backendOutcome = await WaitForBackendOutcomeAsync(
                                    backendPort,
                                    process,
                                    ownershipChecks,
                                    shutdownToken);
                                if (backendOutcome != BackendWaitOutcome.Exited)
                                {
                                    var ownershipLost =
                                        backendOutcome == BackendWaitOutcome.OwnershipLost;
                                    ownershipDefinitivelyLost = ownershipLost;
                                    preserveBackend = !ownershipLost;
                                    publishStopped = false;
                                    await relay.CloseGateAsync();
                                    var detail = ownershipLost
                                        ? "The private listener is no longer owned by the " +
                                            "supervised backend."
                                        : "Private listener ownership could not be inspected; " +
                                            "preserving the verified backend lease.";
                                    statusStore.Write(Program.NewSupervisorStatus(
                                        ownershipLost
                                            ? "backendOwnershipLost"
                                            : "backendOwnershipUnknown",
                                        publicPort,
                                        codexHome,
                                        process.Id,
                                        consecutiveFailures,
                                        lastExitCode: null,
                                        nextRetryAtUtc: null,
                                        detail));
                                    Console.Error.WriteLine(detail);
                                    return 1;
                                }
                            }
                            catch (OperationCanceledException) when (
                                shutdownToken.IsCancellationRequested)
                            {
                            }
                        }
                    }
                    else if (recovered &&
                        !process.HasExited &&
                        !shutdownToken.IsCancellationRequested)
                    {
                        preserveBackend = true;
                        publishStopped = false;
                        const string detail =
                            "The verified recovered backend is not ready; refusing to replace it.";
                        statusStore.Write(Program.NewSupervisorStatus(
                            "recoveredBackendUnavailable",
                            publicPort,
                            codexHome,
                            process.Id,
                            consecutiveFailures,
                            lastExitCode: null,
                            nextRetryAtUtc: null,
                            detail));
                        Console.Error.WriteLine(detail);
                        return 1;
                    }
                    lifecycleCompleted = true;
                }
                catch when (
                    recovered &&
                    !ownershipDefinitivelyLost &&
                    !shutdownToken.IsCancellationRequested)
                {
                    preserveBackend = true;
                    publishStopped = false;
                    throw;
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
                        Volatile.Write(ref activeBackendProcessId, 0);
                        if (process.HasExited)
                        {
                            leaseStore.Delete();
                            leaseActive = false;
                        }
                        if (shutdownToken.IsCancellationRequested && leaseActive)
                        {
                            if (!process.HasExited)
                            {
                                process.Kill();
                                await process.WaitForExitAsync();
                            }
                            leaseStore.Delete();
                            leaseActive = false;
                        }
                        if (!lifecycleCompleted && !preserveBackend && leaseActive)
                        {
                            if (!process.HasExited)
                            {
                                process.Kill();
                                await process.WaitForExitAsync();
                            }
                            leaseStore.Delete();
                            leaseActive = false;
                        }
                        if (!leaseActive && !process.HasExited)
                        {
                            process.Kill();
                            await process.WaitForExitAsync();
                        }
                        if (!preserveBackend)
                        {
                            await Program.AwaitLogPumpsAsync(stdout, stderr);
                        }
                    }
                }

                if (shutdownToken.IsCancellationRequested)
                {
                    break;
                }

                var uptime = DateTimeOffset.UtcNow - startedAt;
                consecutiveFailures = uptime >= TimeSpan.FromMinutes(2)
                    ? 1
                    : consecutiveFailures + 1;
                var delay = delayForFailure(consecutiveFailures);
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
                bool restart;
                try
                {
                    restart = await waitForRestart(delay, shutdownToken);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    break;
                }
                if (!restart)
                {
                    break;
                }
                backendPort = Program.FindAvailablePort(publicPort, backendPort);
            }
        }
        finally
        {
            if (recoveredBackend is not null)
            {
                try
                {
                    if (shutdownToken.IsCancellationRequested)
                    {
                        if (!recoveredBackend.HasExited)
                        {
                            recoveredBackend.Kill();
                            await recoveredBackend.WaitForExitAsync();
                        }
                        leaseStore.Delete();
                    }
                }
                finally
                {
                    recoveredBackend.Dispose();
                }
            }
            if (publishStopped)
            {
                statusStore.Write(Program.NewSupervisorStatus(
                    "stopped",
                    publicPort,
                    codexHome,
                    backendProcessId: null,
                    consecutiveFailures,
                    lastExitCode: null,
                    nextRetryAtUtc: null,
                    "Supervisor stopped without changing future-launch configuration."));
            }
        }
        return 0;
    }

    private static BackendOwnership InspectBackendOwnership(
        int port,
        int processId,
        Func<int, int, bool> isListenerOwnedBy)
    {
        try
        {
            return isListenerOwnedBy(port, processId)
                ? BackendOwnership.Owned
                : BackendOwnership.Lost;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or
                System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(
                $"Backend ownership inspection failed: {exception.Message}");
            return BackendOwnership.Unknown;
        }
    }

    private static async Task<BackendWaitOutcome> WaitForBackendOutcomeAsync(
        int port,
        WindowsProcessGroup process,
        BackendOwnershipChecks ownershipChecks,
        CancellationToken cancellationToken)
    {
        while (!process.HasExited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ownership = InspectBackendOwnership(
                port,
                process.Id,
                ownershipChecks.IsListenerOwnedBy);
            if (ownership == BackendOwnership.Unknown)
            {
                return process.HasExited
                    ? BackendWaitOutcome.Exited
                    : BackendWaitOutcome.OwnershipUnknown;
            }
            if (ownership == BackendOwnership.Lost)
            {
                if (!process.HasExited)
                {
                    await Task.WhenAny(
                        process.WaitForExitAsync(),
                        Task.Delay(ownershipChecks.PollInterval, cancellationToken));
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return process.HasExited
                    ? BackendWaitOutcome.Exited
                    : BackendWaitOutcome.OwnershipLost;
            }
            await Task.Delay(ownershipChecks.PollInterval, cancellationToken);
        }
        return BackendWaitOutcome.Exited;
    }

    private enum BackendOwnership
    {
        Owned,
        Lost,
        Unknown,
    }

    private enum BackendWaitOutcome
    {
        Exited,
        OwnershipLost,
        OwnershipUnknown,
    }
}
