using CodexContinuity.Contracts;

namespace CodexContinuity.Tray;

internal enum ContinuityHealth
{
    Healthy,
    Degraded,
    Unavailable,
}

internal sealed record TrayStatusSnapshot(
    ContinuityHealth Health,
    int? ActiveAgentCount,
    string Detail)
{
    internal static TrayStatusSnapshot Unavailable(string detail) =>
        new(ContinuityHealth.Unavailable, null, detail);
}

internal sealed record TrayCommandResult(int ExitCode, string Output, string Error);

internal sealed record TrayBuildIdentity(
    string? Version,
    string? Executable,
    string? Sha256,
    bool Proven,
    string? Detail)
{
    internal static TrayBuildIdentity Unknown(string detail) =>
        new(null, null, null, false, detail);
}

internal sealed record TrayMutationTarget(
    string? Executable,
    string? SelectedExecutable,
    string? ExpectedSha256,
    string? Error)
{
    internal bool Available => Executable is not null;
}

internal sealed class TrayCommandGate
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    internal async Task<T> RunAsync<T>(Func<Task<T>> command, CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return await command();
        }
        finally
        {
            semaphore.Release();
        }
    }
}

internal sealed record ContinuityUpdateSnapshot(
    string? RunningVersion,
    bool RunningProcessObserved,
    string? LatestVersion,
    int ObservedCount,
    int StagedCount,
    int AppliedCount,
    string LatestState,
    string? LastError,
    string? SelectedVersion = null,
    string? RollbackVersion = null,
    DateTimeOffset? LastCheckedAtUtc = null,
    string? LatestReleaseUrl = null,
    string UpdaterReadiness = "unknown",
    string? UpdaterReadinessReason = null,
    TrayBuildIdentity? RunningBuild = null,
    TrayBuildIdentity? SelectedBuild = null,
    TrayBuildIdentity? RollbackBuild = null)
{
    internal static ContinuityUpdateSnapshot Unavailable(string? error = null) =>
        new(null, false, null, 0, 0, 0, ContinuityUpdateCheckStateNames.Unknown, error);
}

internal sealed record TrayDesktopUpdateSnapshot(
    string? InstalledVersion,
    string? AdvertisedVersion,
    bool? ManifestNewerThanInstalled,
    string MicrosoftStoreAvailability,
    string? RecommendedAction,
    string Detail)
{
    internal static TrayDesktopUpdateSnapshot Unavailable(string detail = "Store update status unavailable.") =>
        new(null, null, null, "notChecked", null, detail);

    internal bool ShouldOfferMicrosoftStore =>
        ManifestNewerThanInstalled == true &&
        string.Equals(RecommendedAction, "checkMicrosoftStore", StringComparison.Ordinal);
}

internal sealed record ContinuityApplySnapshot(
    bool AutomaticApplyWhenIdle,
    long PolicyGeneration,
    string State,
    string? TargetVersion,
    DateTimeOffset? IdleSinceUtc,
    string? LastError,
    bool ControlsAvailable,
    string? AvailabilityError,
    int PolicySchemaVersion = 1,
    DateTimeOffset? SnoozedUntilUtc = null,
    TrayActivationWindow? ActivationWindow = null)
{
    internal static ContinuityApplySnapshot Default => new(
        AutomaticApplyWhenIdle: false,
        PolicyGeneration: 0,
        State: ContinuityUpdateApplyStateNames.StagedOnly,
        TargetVersion: null,
        IdleSinceUtc: null,
        LastError: null,
        ControlsAvailable: true,
        AvailabilityError: null,
        PolicySchemaVersion: 1,
        SnoozedUntilUtc: null,
        ActivationWindow: null);

    internal static ContinuityApplySnapshot Unavailable(string error) => new(
        AutomaticApplyWhenIdle: false,
        PolicyGeneration: 0,
        State: "unavailable",
        TargetVersion: null,
        IdleSinceUtc: null,
        LastError: null,
        ControlsAvailable: false,
        AvailabilityError: error,
        PolicySchemaVersion: 1,
        SnoozedUntilUtc: null,
        ActivationWindow: null);
}

internal sealed record TrayActivationWindow(
    int StartMinuteLocal,
    int EndMinuteLocal,
    string TimeZoneId)
{
    internal string Display =>
        $"{FormatMinute(StartMinuteLocal)}-{FormatMinute(EndMinuteLocal)} ({TimeZoneId})";

    private static string FormatMinute(int minute) =>
        $"{minute / 60:00}:{minute % 60:00}";
}
