using System.Text.Json;
using System.Text.RegularExpressions;
using CodexContinuity.Contracts;

namespace CodexContinuity.Tray;

internal static class TrayStatusParser
{
    internal static TrayStatusSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var ready = root.TryGetProperty("ready", out var readyElement) && readyElement.GetBoolean();
        var activeAgentCount = root.TryGetProperty("activeThreadCount", out var countElement) &&
            countElement.ValueKind == JsonValueKind.Number
            ? countElement.GetInt32()
            : (int?)null;
        var supervisorState = root.TryGetProperty("supervisor", out var supervisor) &&
            supervisor.ValueKind == JsonValueKind.Object && (
                supervisor.TryGetProperty("state", out var stateElement) ||
                supervisor.TryGetProperty("State", out stateElement))
                ? stateElement.GetString()
                : null;
        var desktopAppToolsState = ReadNestedState(root, "desktopAppTools");
        var compatibilityState = ReadNestedState(root, "backendCompatibility");
        var appToolsDegraded = desktopAppToolsState is "unavailable" or "reloadFailed";
        var compatibilityPending = compatibilityState is
            "waitingForDesktopClose" or "waitingForStableClose" or "readyToRoll" or "blocked";
        var health = supervisorState == "waitingForCodexExit"
            ? ContinuityHealth.Degraded
            : ready && supervisorState == "running" &&
                !appToolsDegraded && !compatibilityPending
                ? ContinuityHealth.Healthy
                : ready
                    ? ContinuityHealth.Degraded
                    : ContinuityHealth.Unavailable;
        var detail = health switch
        {
            ContinuityHealth.Healthy => "Backend ready",
            ContinuityHealth.Degraded when supervisorState == "waitingForCodexExit" =>
                "Armed; waiting for the current Codex desktop to close naturally",
            ContinuityHealth.Degraded when compatibilityState == "waitingForDesktopClose" =>
                "Backend ready; compatibility refresh waiting for Codex Desktop to close naturally",
            ContinuityHealth.Degraded when compatibilityState is
                "waitingForStableClose" or "readyToRoll" =>
                "Backend ready; compatibility refresh waiting for a safe all-idle rollover",
            ContinuityHealth.Degraded when compatibilityState == "blocked" =>
                "Backend ready; compatibility refresh needs attention",
            ContinuityHealth.Degraded when desktopAppToolsState == "reloadFailed" =>
                "Backend ready; Desktop tools refresh will retry",
            ContinuityHealth.Degraded when desktopAppToolsState == "unavailable" =>
                "Backend ready; Desktop tools are temporarily unavailable",
            ContinuityHealth.Degraded => $"Backend ready; supervisor {supervisorState ?? "state unknown"}",
            ContinuityHealth.Unavailable => "Backend unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(health), health, null),
        };
        return new TrayStatusSnapshot(health, activeAgentCount, detail);
    }

    private static string? ReadNestedState(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return ReadString(value, "state") ?? ReadString(value, "State");
    }

    internal static ContinuityUpdateSnapshot ParseUpdate(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ContinuityUpdateSnapshot.Unavailable("Update status is invalid.");
        }
        try
        {
            var lastError = ReadString(root, "lastError");
            var readiness = ReadString(root, "updaterReadiness") ??
                InferUpdaterReadiness(lastError);
            var readinessReason = ReadString(root, "updaterReadinessReason") ??
                InferUpdaterReadinessReason(lastError, readiness);
            var latestVersion = ReadString(root, "latestVersion");
            var selectedVersion = ReadString(root, "selectedVersion");
            return new ContinuityUpdateSnapshot(
                ReadString(root, "runningVersion"),
                ReadBool(root, "runningProcessObserved"),
                latestVersion,
                ReadInt(root, "observedCount"),
                ReadInt(root, "stagedCount"),
                ReadInt(root, "appliedCount"),
                ReadString(root, "latestState") ?? ContinuityUpdateCheckStateNames.Unknown,
                lastError,
                SelectedVersion: selectedVersion,
                RollbackVersion: ReadString(root, "rollbackVersion"),
                LastCheckedAtUtc: ReadDateTimeOffset(root, "lastCheckedAtUtc"),
                LatestReleaseUrl: ReadString(root, "latestReleaseUrl"),
                UpdaterReadiness: readiness,
                UpdaterReadinessReason: readinessReason,
                RunningBuild: ParseBuild(root, "runningBuild"),
                SelectedBuild: ParseBuild(root, "selectedBuild"),
                RollbackBuild: ParseRollbackBuild(root, selectedVersion));
        }
        catch (InvalidOperationException)
        {
            return ContinuityUpdateSnapshot.Unavailable("Update status is invalid.");
        }
    }

    internal static TrayDesktopUpdateSnapshot ParseDesktopUpdate(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("codexDesktopUpdate", out var desktop) ||
                desktop.ValueKind != JsonValueKind.Object)
            {
                return TrayDesktopUpdateSnapshot.Unavailable(
                    "Codex Desktop update status is missing from probe output.");
            }
            bool? newer = desktop.TryGetProperty(
                "manifestNewerThanInstalled",
                out var newerElement)
                    ? newerElement.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null,
                    }
                    : null;
            return new(
                ReadString(desktop, "installedVersion"),
                ReadString(desktop, "advertisedVersion"),
                newer,
                ReadString(desktop, "microsoftStoreAvailability") ?? "notChecked",
                ReadString(desktop, "recommendedAction"),
                ReadString(desktop, "detail") ?? "Codex Desktop update status unavailable.");
        }
        catch (JsonException)
        {
            return TrayDesktopUpdateSnapshot.Unavailable(
                "Codex Desktop probe output is invalid.");
        }
    }

    internal static ContinuityApplySnapshot ParseApply(string? policyJson, string? statusJson)
    {
        var automaticApply = false;
        var generation = 0L;
        var policySchemaVersion = 1;
        DateTimeOffset? snoozedUntilUtc = null;
        TrayActivationWindow? activationWindow = null;
        if (policyJson is not null)
        {
            try
            {
                using var policyDocument = JsonDocument.Parse(policyJson);
                var policy = policyDocument.RootElement;
                if (policy.ValueKind != JsonValueKind.Object ||
                    !policy.TryGetProperty("schemaVersion", out var schema) ||
                    !schema.TryGetInt32(out var schemaVersion) ||
                    schemaVersion is not (1 or 2) ||
                    !policy.TryGetProperty("automaticApplyWhenIdle", out var enabled) ||
                    enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !policy.TryGetProperty("generation", out var generationElement) ||
                    !generationElement.TryGetInt64(out generation) || generation < 0)
                {
                    return ContinuityApplySnapshot.Unavailable(
                        "Automatic-apply policy is invalid or from a newer version.");
                }
                policySchemaVersion = schemaVersion;
                automaticApply = enabled.GetBoolean();
                if (schemaVersion == 2)
                {
                    if (policy.TryGetProperty("snoozedUntilUtc", out var snoozeElement))
                    {
                        if (snoozeElement.ValueKind != JsonValueKind.Null &&
                            (snoozeElement.ValueKind != JsonValueKind.String ||
                             !snoozeElement.TryGetDateTimeOffset(out var parsedSnooze)))
                        {
                            return ContinuityApplySnapshot.Unavailable(
                                "Automatic-apply policy contains an invalid snooze.");
                        }
                        snoozedUntilUtc = snoozeElement.ValueKind == JsonValueKind.Null
                            ? null
                            : snoozeElement.GetDateTimeOffset();
                    }
                    if (policy.TryGetProperty("activationWindow", out var windowElement) &&
                        windowElement.ValueKind != JsonValueKind.Null)
                    {
                        if (windowElement.ValueKind != JsonValueKind.Object ||
                            !windowElement.TryGetProperty(
                                "startMinuteLocal",
                                out var startElement) ||
                            !startElement.TryGetInt32(out var startMinute) ||
                            !windowElement.TryGetProperty(
                                "endMinuteLocal",
                                out var endElement) ||
                            !endElement.TryGetInt32(out var endMinute) ||
                            !windowElement.TryGetProperty(
                                "timeZoneId",
                                out var timeZoneElement) ||
                            timeZoneElement.ValueKind != JsonValueKind.String ||
                            timeZoneElement.GetString() is not { Length: > 0 and <= 128 } timeZoneId ||
                            startMinute is < 0 or >= 24 * 60 ||
                            endMinute is < 0 or >= 24 * 60 ||
                            startMinute == endMinute)
                        {
                            return ContinuityApplySnapshot.Unavailable(
                                "Automatic-apply policy contains an invalid activation window.");
                        }
                        try
                        {
                            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                        }
                        catch (Exception exception) when (
                            exception is TimeZoneNotFoundException or InvalidTimeZoneException)
                        {
                            return ContinuityApplySnapshot.Unavailable(
                                "Automatic-apply policy contains an unavailable time zone.");
                        }
                        activationWindow = new(startMinute, endMinute, timeZoneId);
                    }
                }
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidOperationException or FormatException)
            {
                return ContinuityApplySnapshot.Unavailable(
                    "Automatic-apply policy is invalid.");
            }
        }

        if (statusJson is null)
        {
            return ContinuityApplySnapshot.Default with
            {
                AutomaticApplyWhenIdle = automaticApply,
                PolicyGeneration = generation,
                State = automaticApply
                    ? ContinuityUpdateApplyStateNames.Waiting
                    : ContinuityUpdateApplyStateNames.StagedOnly,
                PolicySchemaVersion = policySchemaVersion,
                SnoozedUntilUtc = snoozedUntilUtc,
                ActivationWindow = activationWindow,
            };
        }

        try
        {
            using var statusDocument = JsonDocument.Parse(statusJson);
            var status = statusDocument.RootElement;
            if (status.ValueKind != JsonValueKind.Object ||
                !status.TryGetProperty("schemaVersion", out var schema) ||
                !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1 ||
                !status.TryGetProperty("state", out var stateElement) ||
                stateElement.ValueKind != JsonValueKind.String ||
                stateElement.GetString() is not { } state ||
                !status.TryGetProperty("policyGeneration", out var statusGenerationElement) ||
                !statusGenerationElement.TryGetInt64(out var statusGeneration) ||
                statusGeneration < 0 ||
                !ContinuityUpdateApplyStateNames.IsKnown(state))
            {
                return ContinuityApplySnapshot.Unavailable(
                    "Activation status is invalid or from a newer version.") with
                {
                    AutomaticApplyWhenIdle = automaticApply,
                    PolicyGeneration = generation,
                    PolicySchemaVersion = policySchemaVersion,
                    SnoozedUntilUtc = snoozedUntilUtc,
                    ActivationWindow = activationWindow,
                };
            }
            if (statusGeneration != generation)
            {
                return ContinuityApplySnapshot.Default with
                {
                    AutomaticApplyWhenIdle = automaticApply,
                    PolicyGeneration = generation,
                    State = automaticApply
                        ? ContinuityUpdateApplyStateNames.Waiting
                        : ContinuityUpdateApplyStateNames.StagedOnly,
                    TargetVersion = ReadString(status, "targetVersion"),
                    PolicySchemaVersion = policySchemaVersion,
                    SnoozedUntilUtc = snoozedUntilUtc,
                    ActivationWindow = activationWindow,
                };
            }
            return new(
                automaticApply,
                generation,
                state,
                ReadString(status, "targetVersion"),
                ReadDateTimeOffset(status, "idleSinceUtc"),
                ReadString(status, "lastError"),
                ControlsAvailable: true,
                AvailabilityError: null,
                PolicySchemaVersion: policySchemaVersion,
                SnoozedUntilUtc: snoozedUntilUtc,
                ActivationWindow: activationWindow);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or FormatException)
        {
            return ContinuityApplySnapshot.Unavailable("Activation status is invalid.") with
            {
                AutomaticApplyWhenIdle = automaticApply,
                PolicyGeneration = generation,
                PolicySchemaVersion = policySchemaVersion,
                SnoozedUntilUtc = snoozedUntilUtc,
                ActivationWindow = activationWindow,
            };
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.GetBoolean();

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : null;

    private static TrayBuildIdentity? ParseBuild(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var version = ReadString(value, "version");
        var executable = ReadString(value, "executable");
        var sha256 = ReadString(value, "sha256");
        var proven = value.TryGetProperty("proven", out var provenElement) &&
            provenElement.ValueKind == JsonValueKind.True;
        return new(version, executable, sha256, proven, ReadString(value, "detail"));
    }

    private static TrayBuildIdentity? ParseRollbackBuild(
        JsonElement root,
        string? selectedVersion)
    {
        var direct = ParseBuild(root, "rollbackBuild");
        if (direct?.Sha256 is not null)
        {
            return direct;
        }
        if (!root.TryGetProperty("releases", out var releases) ||
            releases.ValueKind != JsonValueKind.Array)
        {
            return direct;
        }

        JsonElement? selectedRelease = null;
        JsonElement? stagedRelease = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object ||
                !release.TryGetProperty(
                    "rollbackExecutableSha256",
                    out var digestElement) ||
                digestElement.ValueKind != JsonValueKind.String ||
                digestElement.GetString() is not { Length: 64 } ||
                !digestElement.GetString()!.All(Uri.IsHexDigit))
            {
                continue;
            }
            var version = ReadString(release, "version");
            if (selectedVersion is not null &&
                string.Equals(version, selectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                selectedRelease = release;
                break;
            }
            if (stagedRelease is null &&
                release.TryGetProperty("stagedAtUtc", out var stagedAt) &&
                stagedAt.ValueKind != JsonValueKind.Null)
            {
                stagedRelease = release;
            }
        }

        var candidate = selectedRelease ?? stagedRelease;
        if (candidate is null)
        {
            return direct;
        }
        var sha256 = ReadString(candidate.Value, "rollbackExecutableSha256");
        return sha256 is null
            ? direct
            : new(
                Version: null,
                Executable: null,
                Sha256: sha256,
                Proven: false,
                Detail: "Rollback digest recorded in update history.");
    }

    private static string InferUpdaterReadiness(string? lastError)
    {
        if (IsUnsignedBuildError(lastError))
        {
            return "unsignedInstalledBuild";
        }
        if (lastError is not null)
        {
            return "feedUnavailable";
        }
        return "unknown";
    }

    private static string? InferUpdaterReadinessReason(string? lastError, string readiness) =>
        IsUnsignedBuildError(lastError)
            ? "The installed Continuity executable has no valid Authenticode signature."
            : readiness == "feedUnavailable" ? lastError : null;

    private static bool IsUnsignedBuildError(string? error) =>
        error is not null &&
        (error.Contains("unsigned or development", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("does not have a valid Authenticode signature", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("Authenticode verification requires a trusted build", StringComparison.OrdinalIgnoreCase));

    internal static string? ReleaseUrl(string? version) =>
        version is { Length: > 0 and <= 64 } &&
        Regex.IsMatch(
            version,
            @"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\z",
            RegexOptions.CultureInvariant)
            ? $"https://github.com/YesterdaysLemon/codex-continuity/releases/tag/v{version}"
            : null;

}
