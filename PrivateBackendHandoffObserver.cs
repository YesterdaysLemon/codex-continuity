using System.ComponentModel;
using System.Net.WebSockets;
using System.Text.Json;

namespace CodexContinuity;

internal sealed record PrivateBackendObservationChecks(
    Func<int, int, bool> IsListenerOwnedBy,
    Func<int, TimeSpan, CancellationToken, Task<bool>> IsReady,
    Func<int, int, CancellationToken, Task<IReadOnlyList<ThreadLifecycleStatus>>>
        ReadOwnedLifecycles)
{
    internal static PrivateBackendObservationChecks Native { get; } = new(
        WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy,
        Program.IsReadyAsync,
        static async (port, processId, cancellationToken) =>
        {
            await using var client = await Program.RpcClient.ConnectOwnedAsync(
                LoopbackEndpoint.WebSocketUrl(port),
                processId,
                cancellationToken);
            return await client.ListOwnedThreadLifecyclesAsync(cancellationToken);
        });

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(IsListenerOwnedBy);
        ArgumentNullException.ThrowIfNull(IsReady);
        ArgumentNullException.ThrowIfNull(ReadOwnedLifecycles);
    }
}

internal static class PrivateBackendHandoffObserver
{
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMilliseconds(500);

    internal static async Task<ContinuityHandoffPlan> ObserveAsync(
        string stateDirectory,
        int backendPort,
        int backendProcessId,
        CancellationToken cancellationToken,
        PrivateBackendObservationChecks? checks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        LoopbackEndpoint.ValidatePort(backendPort);
        if (backendProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(backendProcessId));
        }
        checks ??= PrivateBackendObservationChecks.Native;
        checks.Validate();

        var snapshot = await ObserveThreadsAsync(
            backendPort,
            backendProcessId,
            cancellationToken,
            checks);
        cancellationToken.ThrowIfCancellationRequested();
        var updateState = new ContinuityUpdateStateStore(
            ContinuityPaths.UpdateStatusFile(stateDirectory)).Load();
        cancellationToken.ThrowIfCancellationRequested();
        var selectedBuild = ContinuitySelectedBuildReader.Load(stateDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var plan = ContinuityHandoffPlanner.Create(
            snapshot.BackendReady,
            snapshot.Threads,
            updateState,
            selectedBuild);
        cancellationToken.ThrowIfCancellationRequested();
        return plan;
    }

    private static async Task<ContinuityThreadSnapshot> ObserveThreadsAsync(
        int backendPort,
        int backendProcessId,
        CancellationToken cancellationToken,
        PrivateBackendObservationChecks checks)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!checks.IsListenerOwnedBy(backendPort, backendProcessId) ||
                !await checks.IsReady(backendPort, ReadinessTimeout, cancellationToken))
            {
                return new(BackendReady: false, Threads: []);
            }

            var threads = await checks.ReadOwnedLifecycles(
                backendPort,
                backendProcessId,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!checks.IsListenerOwnedBy(backendPort, backendProcessId) ||
                !await checks.IsReady(backendPort, ReadinessTimeout, cancellationToken) ||
                !checks.IsListenerOwnedBy(backendPort, backendProcessId))
            {
                return new(BackendReady: false, Threads: []);
            }

            return new(BackendReady: true, threads);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(BackendReady: false, Threads: []);
        }
        catch (Exception exception) when (
            exception is Win32Exception or IOException or HttpRequestException or JsonException or
                InvalidOperationException or WebSocketException)
        {
            return new(BackendReady: false, Threads: []);
        }
    }
}
