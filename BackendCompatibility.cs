using System.Text.Json;

namespace CodexContinuity;

internal static class BackendCompatibilityStateNames
{
    internal const string Current = "current";
    internal const string WaitingForDesktopClose = "waitingForDesktopClose";
    internal const string WaitingForStableClose = "waitingForStableClose";
    internal const string Blocked = "blocked";
    internal const string ReadyToRoll = "readyToRoll";
}

internal sealed record BackendCompatibilityStatus(
    int SchemaVersion,
    string State,
    bool ExecutableChanged,
    bool BridgeUpgradeRequired,
    DateTimeOffset UpdatedAtUtc,
    string Detail)
{
    internal const int CurrentSchemaVersion = 1;
}

internal sealed record BackendCompatibilityDecision(
    bool RequiresRollover,
    string Reason)
{
    internal static BackendCompatibilityDecision Current { get; } =
        new(false, "The supervised backend launch contract is current.");
}

internal sealed class BackendCompatibilityStatusStore(string path)
{
    private const int MaximumStatusBytes = 16 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions =
        ContinuityJsonSerializerPresets.CamelCaseIndented();

    internal BackendCompatibilityStatus? Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            using var file = BoundedStateFile.Open(path, MaximumStatusBytes);
            var status = JsonSerializer.Deserialize<BackendCompatibilityStatus>(
                file.Read().Span,
                SerializerOptions);
            return IsValid(status) ? status : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                JsonException or NotSupportedException)
        {
            return null;
        }
    }

    internal void Write(BackendCompatibilityStatus status)
    {
        if (!IsValid(status))
        {
            throw new InvalidDataException("Backend compatibility status is structurally invalid.");
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(status, SerializerOptions);
        if (bytes.Length > MaximumStatusBytes)
        {
            throw new InvalidDataException("Backend compatibility status exceeds its size limit.");
        }
        BoundedStateFile.WriteAtomically(path, bytes);
    }

    private static bool IsValid(BackendCompatibilityStatus? status) =>
        status is not null &&
        status.SchemaVersion == BackendCompatibilityStatus.CurrentSchemaVersion &&
        status.State is
            BackendCompatibilityStateNames.Current or
            BackendCompatibilityStateNames.WaitingForDesktopClose or
            BackendCompatibilityStateNames.WaitingForStableClose or
            BackendCompatibilityStateNames.Blocked or
            BackendCompatibilityStateNames.ReadyToRoll &&
        status.UpdatedAtUtc != default &&
        !string.IsNullOrWhiteSpace(status.Detail) &&
        status.Detail.Length <= 2048 &&
        !status.Detail.Any(char.IsControl);
}

internal sealed class BackendCompatibilityMonitor
{
    private readonly BackendCompatibilityStatusStore statusStore;
    private readonly Func<string> findCurrentExecutable;
    private readonly Func<CodexDesktopObservation> observeDesktop;
    private readonly Func<DateTimeOffset> now;
    private readonly TimeSpan inspectionInterval;
    private readonly TimeSpan stableDesktopClosedInterval;
    private DateTimeOffset nextInspectionAtUtc;
    private DateTimeOffset? desktopClosedSinceUtc;
    private string? lastRecordedState;
    private bool? lastRecordedExecutableChanged;
    private bool? lastRecordedBridgeUpgradeRequired;
    private string? lastRecordedDetail;

    internal BackendCompatibilityMonitor(
        string stateDirectory,
        Func<string> findCurrentExecutable,
        Func<CodexDesktopObservation>? observeDesktop = null,
        Func<DateTimeOffset>? now = null,
        TimeSpan? inspectionInterval = null,
        TimeSpan? stableDesktopClosedInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentNullException.ThrowIfNull(findCurrentExecutable);
        this.findCurrentExecutable = findCurrentExecutable;
        this.observeDesktop = observeDesktop ?? CodexDesktopProcesses.Capture;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.inspectionInterval = inspectionInterval ?? TimeSpan.FromSeconds(15);
        this.stableDesktopClosedInterval = stableDesktopClosedInterval ??
            TimeSpan.FromSeconds(3);
        if (this.inspectionInterval <= TimeSpan.Zero ||
            this.inspectionInterval > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(inspectionInterval));
        }
        if (this.stableDesktopClosedInterval <= TimeSpan.Zero ||
            this.stableDesktopClosedInterval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(stableDesktopClosedInterval));
        }
        statusStore = new BackendCompatibilityStatusStore(
            ContinuityPaths.BackendCompatibilityStatusFile(stateDirectory));
    }

    internal Task<BackendCompatibilityDecision> InspectAsync(
        BackendLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();
        var observedAt = now();
        if (observedAt < nextInspectionAtUtc)
        {
            return Task.FromResult(BackendCompatibilityDecision.Current);
        }
        nextInspectionAtUtc = observedAt + inspectionInterval;

        string currentExecutable;
        try
        {
            currentExecutable = Path.GetFullPath(findCurrentExecutable());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                ArgumentException or NotSupportedException)
        {
            desktopClosedSinceUtc = null;
            Write(
                BackendCompatibilityStateNames.Blocked,
                executableChanged: false,
                bridgeUpgradeRequired: false,
                "The current Codex backend executable could not be identified safely.",
                observedAt);
            return Task.FromResult(new BackendCompatibilityDecision(
                false,
                "The current Codex backend executable is unknown."));
        }

        var executableChanged = !Path.GetFullPath(lease.BackendExecutable).Equals(
            currentExecutable,
            StringComparison.OrdinalIgnoreCase);
        var bridgeUpgradeRequired =
            lease.DesktopMcpBridgeVersion != DesktopMcpContractResolver.BridgeVersion;
        if (!executableChanged && !bridgeUpgradeRequired)
        {
            desktopClosedSinceUtc = null;
            Write(
                BackendCompatibilityStateNames.Current,
                executableChanged,
                bridgeUpgradeRequired,
                BackendCompatibilityDecision.Current.Reason,
                observedAt);
            return Task.FromResult(BackendCompatibilityDecision.Current);
        }

        var desktop = observeDesktop();
        if (desktop.Kind == CodexDesktopObservationKind.Unsafe)
        {
            desktopClosedSinceUtc = null;
            Write(
                BackendCompatibilityStateNames.Blocked,
                executableChanged,
                bridgeUpgradeRequired,
                "Backend rollover is blocked because the Store Codex process identity is uncertain.",
                observedAt);
            return Task.FromResult(new BackendCompatibilityDecision(
                false,
                "Store Codex process identity is uncertain."));
        }
        if (desktop.Kind == CodexDesktopObservationKind.Running)
        {
            desktopClosedSinceUtc = null;
            Write(
                BackendCompatibilityStateNames.WaitingForDesktopClose,
                executableChanged,
                bridgeUpgradeRequired,
                "A backend compatibility refresh is pending; Codex Desktop will never be closed or relaunched by Continuity.",
                observedAt);
            return Task.FromResult(new BackendCompatibilityDecision(
                false,
                "Waiting for Codex Desktop to close naturally."));
        }

        desktopClosedSinceUtc ??= observedAt;
        if (observedAt - desktopClosedSinceUtc < stableDesktopClosedInterval)
        {
            nextInspectionAtUtc = observedAt + TimeSpan.FromMilliseconds(500);
            Write(
                BackendCompatibilityStateNames.WaitingForStableClose,
                executableChanged,
                bridgeUpgradeRequired,
                "Codex Desktop is closed; Continuity is proving a stable empty interval before considering rollover.",
                observedAt);
            return Task.FromResult(new BackendCompatibilityDecision(
                false,
                "Waiting for a stable naturally closed Desktop interval."));
        }

        Write(
            BackendCompatibilityStateNames.ReadyToRoll,
            executableChanged,
            bridgeUpgradeRequired,
            "The Desktop is naturally closed; the relay will still require a fresh all-idle proof before graceful rollover.",
            observedAt);
        return Task.FromResult(new BackendCompatibilityDecision(
            true,
            bridgeUpgradeRequired
                ? "The backend needs the normalized Desktop app-tools bridge."
                : "A newer Codex backend executable is available."));
    }

    private void Write(
        string state,
        bool executableChanged,
        bool bridgeUpgradeRequired,
        string detail,
        DateTimeOffset updatedAtUtc)
    {
        if (state == lastRecordedState &&
            executableChanged == lastRecordedExecutableChanged &&
            bridgeUpgradeRequired == lastRecordedBridgeUpgradeRequired &&
            detail == lastRecordedDetail)
        {
            return;
        }
        try
        {
            statusStore.Write(new(
                BackendCompatibilityStatus.CurrentSchemaVersion,
                state,
                executableChanged,
                bridgeUpgradeRequired,
                updatedAtUtc,
                detail));
            lastRecordedState = state;
            lastRecordedExecutableChanged = executableChanged;
            lastRecordedBridgeUpgradeRequired = bridgeUpgradeRequired;
            lastRecordedDetail = detail;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine(
                "Backend compatibility status could not be recorded; runtime safety is unchanged.");
        }
    }
}
