using System.Drawing;
using System.Text.RegularExpressions;
using CodexContinuity.Contracts;

namespace CodexContinuity.Tray;

internal enum TrayNotificationAction
{
    None,
    OpenReleaseNotes,
    OpenDiagnostics,
    RestartBackend,
}

internal sealed record TrayNotification(
    string Key,
    string Title,
    string Body,
    TrayNotificationAction Action);

internal sealed record TrayNotificationSnapshot(
    ContinuityHealth Health,
    string LatestState,
    string? LatestVersion,
    string ApplyState,
    string? ApplyTargetVersion,
    string? ApplyError)
{
    internal static TrayNotificationSnapshot From(
        TrayStatusSnapshot status,
        ContinuityUpdateSnapshot update,
        ContinuityApplySnapshot apply) => new(
            status.Health,
            update.LatestState,
            update.LatestVersion,
            apply.State,
            apply.TargetVersion,
            apply.LastError);
}

internal static class TrayNotificationPlanner
{
    internal static TrayNotification? Plan(
        TrayNotificationSnapshot? previous,
        TrayNotificationSnapshot current,
        string? releaseNotesUrl)
    {
        if (previous is null)
        {
            return null;
        }

        if (current.ApplyState == ContinuityUpdateApplyStateNames.RolledBack &&
            previous.ApplyState != ContinuityUpdateApplyStateNames.RolledBack)
        {
            var target = current.ApplyTargetVersion is null
                ? "the staged update"
                : $"v{current.ApplyTargetVersion}";
            return new(
                $"rollback:{current.ApplyTargetVersion}:{current.ApplyError}",
                "Continuity update rolled back",
                $"Safe activation of {target} rolled back; active agents were preserved.",
                TrayNotificationAction.OpenDiagnostics);
        }

        if (current.ApplyState == ContinuityUpdateApplyStateNames.Failed &&
            previous.ApplyState != ContinuityUpdateApplyStateNames.Failed)
        {
            var target = current.ApplyTargetVersion is null
                ? "the staged update"
                : $"v{current.ApplyTargetVersion}";
            return new(
                $"failure:{current.ApplyTargetVersion}:{current.ApplyError}",
                "Continuity update needs attention",
                $"Safe activation of {target} failed. Review diagnostics before retrying.",
                TrayNotificationAction.OpenDiagnostics);
        }

        if (current.LatestState == ContinuityUpdateCheckStateNames.Staged &&
            (previous.LatestState != ContinuityUpdateCheckStateNames.Staged ||
             !string.Equals(
                 previous.LatestVersion,
                 current.LatestVersion,
                 StringComparison.OrdinalIgnoreCase)))
        {
            var version = current.LatestVersion is null ? "the latest release" : $"v{current.LatestVersion}";
            return new(
                $"staged:{current.LatestVersion}",
                "Continuity update ready",
                $"{version} was verified and staged without stopping Codex.",
                releaseNotesUrl is null
                    ? TrayNotificationAction.None
                    : TrayNotificationAction.OpenReleaseNotes);
        }

        if (current.Health == ContinuityHealth.Healthy &&
            previous.Health != ContinuityHealth.Healthy)
        {
            return new(
                "backend:recovered",
                "Continuity backend recovered",
                "The supervised backend is ready again; active agents were preserved.",
                TrayNotificationAction.None);
        }

        if (current.Health == ContinuityHealth.Unavailable &&
            previous.Health != ContinuityHealth.Unavailable)
        {
            return new(
                "backend:unavailable",
                "Continuity backend unavailable",
                "The tray can offer a safe backend restart; Codex and its active work were not stopped.",
                TrayNotificationAction.RestartBackend);
        }

        return null;
    }
}

internal sealed class TrayNotificationDeduper
{
    private string? lastKey;

    internal bool ShouldShow(TrayNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (string.Equals(lastKey, notification.Key, StringComparison.Ordinal))
        {
            return false;
        }
        lastKey = notification.Key;
        return true;
    }
}

internal static class TrayDiagnosticsFormatter
{
    internal static string Format(
        TrayStatusSnapshot status,
        ContinuityUpdateSnapshot update,
        ContinuityApplySnapshot apply,
        TrayDesktopUpdateSnapshot desktop)
    {
        var lines = new List<string>
        {
            "Codex Continuity diagnostics",
            $"Health: {status.Health}",
            $"Active agents: {status.ActiveAgentCount?.ToString() ?? "unknown"}",
            $"Backend detail: {Compact(status.Detail)}",
            $"Running version: {VersionText(update.RunningBuild, update.RunningVersion)}",
            $"Selected startup version: {VersionText(update.SelectedBuild, update.SelectedVersion)}",
            $"Latest observed version: {update.LatestVersion ?? "unknown"}",
            $"Rollback version: {VersionText(update.RollbackBuild, update.RollbackVersion)}",
            $"Update state: {update.LatestState}",
            $"Updater readiness: {update.UpdaterReadiness} ({Compact(update.UpdaterReadinessReason ?? "no reason recorded")})",
            $"Last update check: {update.LastCheckedAtUtc?.ToString("O") ?? "unknown"}",
            $"Activation: {apply.State}; target {apply.TargetVersion ?? "none"}",
            $"Activation policy: schema {apply.PolicySchemaVersion}; {ScheduleText(apply)}",
            $"Activation detail: {Compact(apply.LastError ?? "none")}",
            $"Codex Desktop: {desktop.InstalledVersion ?? "unknown"} installed; " +
                $"{desktop.AdvertisedVersion ?? "unknown"} advertised",
            $"Microsoft Store availability: {desktop.MicrosoftStoreAvailability}",
        };
        return string.Join(Environment.NewLine, lines);
    }

    private static string VersionText(TrayBuildIdentity? identity, string? fallback) =>
        identity?.Version ?? fallback ?? "unknown";

    private static string ScheduleText(ContinuityApplySnapshot apply)
    {
        var snooze = apply.SnoozedUntilUtc is { } until
            ? $"snoozed until {until:O}"
            : "not snoozed";
        var window = apply.ActivationWindow?.Display ?? "any time";
        return $"{snooze}; window {window}";
    }

    private static string Compact(string text)
    {
        const int maximumLength = 240;
        var singleLine = RedactPaths(text.Replace('\r', ' ').Replace('\n', ' ').Trim());
        return singleLine.Length <= maximumLength
            ? singleLine
            : $"{singleLine[..maximumLength]}\u2026";
    }

    private static string RedactPaths(string text) => Regex.Replace(
        text,
        @"(?i)(?:[a-z]:\\|\\\\)[^,;|]+",
        "<path>");
}

internal static class TrayStatusPresentation
{
    internal static Icon IconForHealth(ContinuityHealth health, Icon applicationIcon)
    {
        ArgumentNullException.ThrowIfNull(applicationIcon);
        return health switch
        {
            ContinuityHealth.Healthy or
            ContinuityHealth.Degraded or
            ContinuityHealth.Unavailable => applicationIcon,
            _ => throw new ArgumentOutOfRangeException(nameof(health), health, null),
        };
    }

    internal static bool ShowRecovery(ContinuityHealth health) => health == ContinuityHealth.Unavailable;

    internal static string UpdateCounts(ContinuityUpdateSnapshot update) =>
        $"Updates: {update.ObservedCount} observed / {update.StagedCount} staged / " +
        $"{update.AppliedCount} applied";

    internal static bool ShowRollback(ContinuityUpdateSnapshot update) =>
        update.RollbackBuild?.Proven == true &&
        update.RollbackVersion is not null;

    internal static bool ShowMicrosoftStoreUpdate(TrayDesktopUpdateSnapshot desktop) =>
        desktop.ShouldOfferMicrosoftStore;

    internal static string VersionDetail(ContinuityUpdateSnapshot update)
    {
        var running = VersionLabel("Running", update.RunningBuild, update.RunningVersion);
        var selected = VersionLabel(
            "Startup target",
            update.SelectedBuild,
            update.SelectedVersion);
        var latest = update.LatestVersion is null
            ? "Latest observed: unknown"
            : $"Latest observed: v{update.LatestVersion}";
        var rollback = VersionLabel(
            "Rollback",
            update.RollbackBuild,
            update.RollbackVersion);
        return string.Join("; ", running, selected, latest, rollback);
    }

    internal static string UpdaterReadinessDetail(ContinuityUpdateSnapshot update) =>
        update.UpdaterReadiness switch
        {
            "ready" => "Updater readiness: verified staging is available",
            "unsignedInstalledBuild" =>
                "Updater readiness: automatic staging unavailable; installed build is unsigned",
            "feedUnavailable" =>
                $"Updater readiness: release feed unavailable{ErrorSuffix(update.UpdaterReadinessReason)}",
            "stateInvalid" =>
                $"Updater readiness: persisted state is invalid{ErrorSuffix(update.UpdaterReadinessReason)}",
            _ => "Updater readiness: not yet proven",
        };

    internal static string UpdateDetail(ContinuityUpdateSnapshot update, ContinuityHealth health)
    {
        var currentVersion = update.RunningProcessObserved && health == ContinuityHealth.Healthy
            ? $"Running v{update.RunningVersion}"
            : $"Last ran v{update.RunningVersion}";
        var versions = update.RunningVersion is null
            ? "Update tracking unavailable"
            : update.LatestVersion is null
                ? $"{currentVersion}; latest release unknown"
                : $"{currentVersion}; latest v{update.LatestVersion}";
        if (update.LastError is not null)
        {
            return $"{versions}; last check failed: {Compact(update.LastError)}";
        }
        if (update.RunningVersion is null || update.LatestVersion is null)
        {
            return versions;
        }
        return update.LatestState switch
        {
            ContinuityUpdateCheckStateNames.Active => $"{currentVersion}; latest is active",
            ContinuityUpdateCheckStateNames.Staged => $"{currentVersion}; v{update.LatestVersion} staged",
            ContinuityUpdateCheckStateNames.Deferred => $"{currentVersion}; v{update.LatestVersion} deferred by rollback",
            ContinuityUpdateCheckStateNames.Inactive => $"{currentVersion}; latest v{update.LatestVersion} is not active",
            ContinuityUpdateCheckStateNames.Ahead => $"{currentVersion}; ahead of stable v{update.LatestVersion}",
            ContinuityUpdateCheckStateNames.Failed => $"{currentVersion}; v{update.LatestVersion} could not be staged",
            ContinuityUpdateCheckStateNames.Observed => $"{currentVersion}; v{update.LatestVersion} observed; staging pending",
            ContinuityUpdateCheckStateNames.Unknown => $"{currentVersion}; update state unknown",
            _ => $"{currentVersion}; update state {update.LatestState}",
        };
    }

    internal static string ApplyDetail(ContinuityApplySnapshot apply) =>
        ApplyDetail(apply, DateTimeOffset.UtcNow);

    internal static string ApplyDetail(
        ContinuityApplySnapshot apply,
        DateTimeOffset nowUtc)
    {
        if (!apply.ControlsAvailable)
        {
            return $"Activation controls unavailable: {Compact(
                apply.AvailabilityError ?? "state could not be read")}";
        }
        var target = apply.TargetVersion is null ? "the staged update" : $"v{apply.TargetVersion}";
        var detail = apply.State switch
        {
            ContinuityUpdateApplyStateNames.StagedOnly when apply.AutomaticApplyWhenIdle =>
                "Activation: automatic apply enabled; awaiting supervisor status",
            ContinuityUpdateApplyStateNames.StagedOnly => "Activation: staged only; automatic apply is off",
            ContinuityUpdateApplyStateNames.Waiting when apply.TargetVersion is null =>
                "Activation: waiting for a verified staged update",
            ContinuityUpdateApplyStateNames.Waiting when apply.IdleSinceUtc is null =>
                $"Activation: {target} waiting for a safe idle window",
            ContinuityUpdateApplyStateNames.Waiting => $"Activation: {target} proving a stable idle window",
            ContinuityUpdateApplyStateNames.Applying => $"Activation: handing off to {target}; Codex stays open",
            ContinuityUpdateApplyStateNames.Active => $"Activation: {target} verified active",
            ContinuityUpdateApplyStateNames.RolledBack => $"Activation: {target} rolled back safely{ErrorSuffix(apply)}",
            ContinuityUpdateApplyStateNames.Failed => $"Activation failed for {target}{ErrorSuffix(apply)}",
            _ => $"Activation state: {apply.State}",
        };
        return $"{detail}{ScheduleSuffix(apply, nowUtc)}";
    }

    internal static string ActivationScheduleDetail(
        ContinuityApplySnapshot apply,
        DateTimeOffset nowUtc) =>
        $"Activation schedule: {ScheduleText(apply, nowUtc)}";

    internal static bool ShowApplyRetry(ContinuityApplySnapshot apply) =>
        apply.ControlsAvailable && apply.AutomaticApplyWhenIdle &&
        apply.State is ContinuityUpdateApplyStateNames.Failed or ContinuityUpdateApplyStateNames.RolledBack;

    internal static bool CanChangeApplyPolicy(ContinuityApplySnapshot apply) =>
        apply.ControlsAvailable && apply.State != ContinuityUpdateApplyStateNames.Applying;

    internal static string CommandFailure(string action, TrayCommandResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        detail = string.IsNullOrWhiteSpace(detail) ? $"exit code {result.ExitCode}" : detail;
        return $"{action} failed: {Compact(detail)}";
    }

    private static string ErrorSuffix(ContinuityApplySnapshot apply) =>
        string.IsNullOrWhiteSpace(apply.LastError) ? string.Empty : $": {Compact(apply.LastError)}";

    private static string ErrorSuffix(string? error) =>
        string.IsNullOrWhiteSpace(error) ? string.Empty : $": {Compact(error)}";

    private static string ScheduleSuffix(
        ContinuityApplySnapshot apply,
        DateTimeOffset nowUtc)
    {
        if (apply.SnoozedUntilUtc is null && apply.ActivationWindow is null)
        {
            return string.Empty;
        }
        return $"; {ScheduleText(apply, nowUtc)}";
    }

    private static string ScheduleText(
        ContinuityApplySnapshot apply,
        DateTimeOffset nowUtc)
    {
        var snooze = apply.SnoozedUntilUtc is { } until
            ? until > nowUtc
                ? $"snoozed until {until.UtcDateTime:yyyy-MM-dd HH:mm} UTC"
                : "snooze expired"
            : "not snoozed";
        var window = apply.ActivationWindow?.Display ?? "any time";
        return $"activation {snooze}; window {window}";
    }

    private static string VersionLabel(
        string label,
        TrayBuildIdentity? identity,
        string? fallbackVersion)
    {
        var version = identity?.Version ?? fallbackVersion;
        if (version is null)
        {
            return $"{label}: unknown";
        }
        var proof = identity?.Proven == true ? "proven" : "recorded";
        return $"{label}: v{version} ({proof})";
    }

    private static string Compact(string text)
    {
        const int maximumLength = 160;
        var singleLine = Regex.Replace(
            text.Replace('\r', ' ').Replace('\n', ' ').Trim(),
            @"(?i)(?:[a-z]:\\|\\\\)[^,;|]+",
            "<path>");
        return singleLine.Length <= maximumLength
            ? singleLine
            : $"{singleLine[..maximumLength]}\u2026";
    }
}
