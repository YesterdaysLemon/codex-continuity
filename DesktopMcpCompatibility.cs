using System.Text.Json;

namespace CodexContinuity;

internal static class DesktopMcpBridgeStateNames
{
    internal const string WaitingForDesktop = "waitingForDesktop";
    internal const string Unavailable = "unavailable";
    internal const string ReloadQueued = "reloadQueued";
    internal const string Ready = "ready";
    internal const string ReloadFailed = "reloadFailed";
}

internal sealed record DesktopMcpBridgeStatus(
    int SchemaVersion,
    string State,
    string? ContractFingerprint,
    int? DesktopProcessId,
    DateTimeOffset UpdatedAtUtc,
    string Detail)
{
    internal const int CurrentSchemaVersion = 1;
}

internal sealed class DesktopMcpBridgeStatusStore(string path)
{
    private const int MaximumStatusBytes = 16 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions =
        ContinuityJsonSerializerPresets.CamelCaseIndented();

    internal DesktopMcpBridgeStatus? Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            using var file = BoundedStateFile.Open(path, MaximumStatusBytes);
            var status = JsonSerializer.Deserialize<DesktopMcpBridgeStatus>(
                file.Read().Span,
                SerializerOptions);
            return IsValid(status) ? status : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    internal void Write(DesktopMcpBridgeStatus status)
    {
        if (!IsValid(status))
        {
            throw new InvalidDataException("Desktop app-tools status is structurally invalid.");
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(status, SerializerOptions);
        if (bytes.Length > MaximumStatusBytes)
        {
            throw new InvalidDataException("Desktop app-tools status exceeds its size limit.");
        }
        BoundedStateFile.WriteAtomically(path, bytes);
    }

    private static bool IsValid(DesktopMcpBridgeStatus? status) =>
        status is not null &&
        status.SchemaVersion == DesktopMcpBridgeStatus.CurrentSchemaVersion &&
        status.State is
            DesktopMcpBridgeStateNames.WaitingForDesktop or
            DesktopMcpBridgeStateNames.Unavailable or
            DesktopMcpBridgeStateNames.ReloadQueued or
            DesktopMcpBridgeStateNames.Ready or
            DesktopMcpBridgeStateNames.ReloadFailed &&
        (status.ContractFingerprint is null ||
            status.ContractFingerprint.Length == 64 &&
            status.ContractFingerprint.All(Uri.IsHexDigit)) &&
        status.DesktopProcessId is null or > 0 &&
        status.UpdatedAtUtc != default &&
        !string.IsNullOrWhiteSpace(status.Detail) &&
        status.Detail.Length <= 2048 &&
        !status.Detail.Any(char.IsControl);
}

internal sealed class DesktopMcpReloadMonitor
{
    private readonly DesktopMcpBridgeStatusStore statusStore;
    private readonly Func<CancellationToken, Task<DesktopMcpContractResult>> resolve;
    private readonly Func<int, int, CancellationToken, Task> reload;
    private readonly Func<DateTimeOffset> now;
    private readonly TimeSpan interval;
    private DateTimeOffset nextCheckAtUtc;
    private string? appliedFingerprint;
    private string? lastRecordedState;
    private string? lastRecordedFingerprint;
    private int? lastRecordedDesktopProcessId;
    private string? lastRecordedDetail;

    internal DesktopMcpReloadMonitor(
        string stateDirectory,
        Func<CancellationToken, Task<DesktopMcpContractResult>>? resolve = null,
        Func<int, int, CancellationToken, Task>? reload = null,
        Func<DateTimeOffset>? now = null,
        TimeSpan? interval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        this.resolve = resolve ?? (cancellationToken =>
            DesktopMcpContractResolver.ResolveAsync(cancellationToken));
        this.reload = reload ?? ReloadOwnedBackendAsync;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.interval = interval ?? TimeSpan.FromSeconds(10);
        if (this.interval <= TimeSpan.Zero || this.interval > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }
        statusStore = new DesktopMcpBridgeStatusStore(
            ContinuityPaths.DesktopMcpBridgeStatusFile(stateDirectory));
    }

    internal async Task TryRefreshAsync(
        BackendLease lease,
        int backendPort,
        int backendProcessId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var observedAt = now();
        if (observedAt < nextCheckAtUtc)
        {
            return;
        }
        nextCheckAtUtc = observedAt + interval;
        if (lease.DesktopMcpBridgeVersion != DesktopMcpContractResolver.BridgeVersion)
        {
            Write(
                DesktopMcpBridgeStateNames.Unavailable,
                fingerprint: null,
                desktopProcessId: null,
                "Desktop tools will become available after the pending safe backend compatibility refresh.",
                observedAt);
            return;
        }

        DesktopMcpContractResult discovery;
        try
        {
            discovery = await resolve(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                JsonException or TimeoutException or InvalidOperationException)
        {
            Write(
                DesktopMcpBridgeStateNames.Unavailable,
                fingerprint: null,
                desktopProcessId: null,
                "The current Desktop app-tools contract could not be inspected safely.",
                observedAt);
            return;
        }

        if (!discovery.IsAvailable)
        {
            Write(
                discovery.Kind == DesktopMcpContractKind.DesktopNotRunning
                    ? DesktopMcpBridgeStateNames.WaitingForDesktop
                    : DesktopMcpBridgeStateNames.Unavailable,
                fingerprint: null,
                desktopProcessId: null,
                discovery.Detail,
                observedAt);
            return;
        }

        var contract = discovery.Contract!;
        if (contract.Fingerprint.Equals(appliedFingerprint, StringComparison.Ordinal))
        {
            Write(
                DesktopMcpBridgeStateNames.Ready,
                contract.Fingerprint,
                contract.DesktopProcessId,
                "The supervised backend is aligned with the current Desktop app-tools session.",
                observedAt);
            return;
        }

        try
        {
            await reload(backendPort, backendProcessId, cancellationToken);
            appliedFingerprint = contract.Fingerprint;
            Write(
                DesktopMcpBridgeStateNames.ReloadQueued,
                contract.Fingerprint,
                contract.DesktopProcessId,
                "The current Desktop app-tools contract was queued for each loaded thread's next active turn.",
                now());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or HttpRequestException or
                InvalidOperationException or System.Net.WebSockets.WebSocketException or
                TimeoutException)
        {
            Write(
                DesktopMcpBridgeStateNames.ReloadFailed,
                contract.Fingerprint,
                contract.DesktopProcessId,
                "The backend rejected the safe app-tools refresh; Continuity will retry without restarting it.",
                now());
        }
    }

    private static async Task ReloadOwnedBackendAsync(
        int backendPort,
        int backendProcessId,
        CancellationToken cancellationToken)
    {
        await using var client = await RpcClient.ConnectOwnedAsync(
            LoopbackEndpoint.WebSocketUrl(backendPort),
            backendProcessId,
            cancellationToken);
        await client.ReloadOwnedMcpServersAsync(cancellationToken);
    }

    private void Write(
        string state,
        string? fingerprint,
        int? desktopProcessId,
        string detail,
        DateTimeOffset updatedAtUtc)
    {
        if (state == lastRecordedState &&
            fingerprint == lastRecordedFingerprint &&
            desktopProcessId == lastRecordedDesktopProcessId &&
            detail == lastRecordedDetail)
        {
            return;
        }
        try
        {
            statusStore.Write(new(
                DesktopMcpBridgeStatus.CurrentSchemaVersion,
                state,
                fingerprint,
                desktopProcessId,
                updatedAtUtc,
                detail));
            lastRecordedState = state;
            lastRecordedFingerprint = fingerprint;
            lastRecordedDesktopProcessId = desktopProcessId;
            lastRecordedDetail = detail;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine(
                "Desktop app-tools status could not be recorded; runtime safety is unchanged.");
        }
    }
}
