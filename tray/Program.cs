using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexContinuity.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(
            initiallyOwned: true,
            "Local\\CodexContinuity-Tray",
            out var ownsMutex);
        if (!ownsMutex)
        {
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new ContinuityTrayContext());
    }
}

internal sealed class ContinuityTrayContext : ApplicationContext
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(20);
    private readonly CancellationTokenSource shutdown = new();
    private readonly NotifyIcon notifyIcon;
    private readonly ToolStripMenuItem healthItem;
    private readonly ToolStripMenuItem agentsItem;
    private readonly ToolStripMenuItem updateItem;
    private readonly ToolStripMenuItem updateDetailItem;
    private readonly ToolStripMenuItem versionDetailItem;
    private readonly ToolStripMenuItem readinessItem;
    private readonly ToolStripMenuItem applyDetailItem;
    private readonly ToolStripMenuItem activationScheduleItem;
    private readonly ToolStripMenuItem automaticApplyItem;
    private readonly ToolStripMenuItem retryApplyItem;
    private readonly ToolStripMenuItem snoozeMenu;
    private readonly ToolStripMenuItem clearSnoozeItem;
    private readonly ToolStripMenuItem activationWindowMenu;
    private readonly ToolStripMenuItem customActivationWindowItem;
    private readonly ToolStripMenuItem clearActivationWindowItem;
    private readonly ToolStripMenuItem checkForUpdatesItem;
    private readonly ToolStripMenuItem recoveryItem;
    private readonly ToolStripMenuItem rollbackItem;
    private readonly ToolStripMenuItem releaseNotesItem;
    private readonly ToolStripMenuItem storeUpdateItem;
    private readonly ToolStripMenuItem copyDiagnosticsItem;
    private readonly ToolStripMenuItem recentActivityItem;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly TrayStatusClient statusClient;
    private readonly Icon applicationIcon;
    private readonly TrayMenuHost menuHost;
    private readonly TrayNotificationDeduper notificationDeduper = new();
    private readonly TrayActivityHistoryStore activityHistory;
    private bool refreshInProgress;
    private bool applyPolicyMutable;
    private bool applyRetryAvailable;
    private string? latestReleaseUrl;
    private TrayStatusSnapshot? lastStatus;
    private ContinuityUpdateSnapshot? lastUpdate;
    private ContinuityApplySnapshot? lastApply;
    private TrayDesktopUpdateSnapshot? lastDesktopUpdate;
    private TrayNotificationSnapshot? previousNotificationSnapshot;
    private TrayNotificationAction notificationAction;
    private readonly TrayMutationPresenter mutationPresenter = new();

    internal ContinuityTrayContext()
    {
        var applicationDirectory = AppContext.BaseDirectory;
        var supervisorExecutable = TrayStatusClient.ResolveSupervisorExecutable(
            applicationDirectory);
        statusClient = new TrayStatusClient(supervisorExecutable, applicationDirectory);
        activityHistory = new TrayActivityHistoryStore(statusClient.ActivityHistoryPath);
        applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? SystemIcons.Application;
        healthItem = new ToolStripMenuItem("Checking backend…") { Enabled = false };
        agentsItem = new ToolStripMenuItem("Active agents: checking…") { Enabled = false };
        updateItem = new ToolStripMenuItem("Updates: checking…") { Enabled = false };
        updateDetailItem = new ToolStripMenuItem("Update state: checking…") { Enabled = false };

        versionDetailItem = new ToolStripMenuItem("Versions: checking...") { Enabled = false };
        readinessItem = new ToolStripMenuItem("Updater readiness: checking...") { Enabled = false };
        applyDetailItem = new ToolStripMenuItem("Activation: checking...") { Enabled = false };
        activationScheduleItem = new ToolStripMenuItem("Activation schedule: checking...") { Enabled = false };
        automaticApplyItem = MenuItem(
            "Apply Continuity updates when idle (Codex stays open)",
            async () => await ToggleAutomaticApplyAsync());
        automaticApplyItem.CheckOnClick = false;
        automaticApplyItem.Enabled = false;
        retryApplyItem = MenuItem(
            "Retry safe update activation",
            async () => await RetryAutomaticApplyAsync());
        retryApplyItem.Visible = false;
        snoozeMenu = new ToolStripMenuItem("Snooze activation");
        snoozeMenu.Enabled = false;
        snoozeMenu.DropDownItems.Add(
            MenuItem("Snooze for 1 hour", () => SnoozeAsync(60)));
        snoozeMenu.DropDownItems.Add(
            MenuItem("Snooze for 4 hours", () => SnoozeAsync(4 * 60)));
        snoozeMenu.DropDownItems.Add(
            MenuItem("Snooze for 24 hours", () => SnoozeAsync(24 * 60)));
        clearSnoozeItem = MenuItem("Resume activation now", ClearSnoozeAsync);
        clearSnoozeItem.Visible = false;
        activationWindowMenu = new ToolStripMenuItem("Set activation window");
        activationWindowMenu.Enabled = false;
        activationWindowMenu.DropDownItems.Add(
            MenuItem("Allow activation at any local time", ClearActivationWindowAsync));
        activationWindowMenu.DropDownItems.Add(
            MenuItem("Use 22:00-07:00 local window", SetDefaultActivationWindowAsync));
        customActivationWindowItem = MenuItem(
            "Custom\u2026",
            ShowCustomActivationWindowAsync);
        clearActivationWindowItem = MenuItem(
            "Clear activation window",
            ClearActivationWindowAsync);
        clearActivationWindowItem.Visible = false;
        activationWindowMenu.DropDownItems.Add(customActivationWindowItem);

        checkForUpdatesItem = MenuItem(
            "Check for updates now",
            async () => await CheckForUpdatesAsync());
        recoveryItem = MenuItem(
            "Restart Continuity backend",
            async () => await RestartSupervisorAsync());
        recoveryItem.Visible = false;
        rollbackItem = MenuItem(
            "Rollback Continuity on next safe start",
            async () => await RollbackAsync());
        rollbackItem.Visible = false;
        releaseNotesItem = MenuItem("View release notes", OpenReleaseNotes);
        releaseNotesItem.Visible = false;
        storeUpdateItem = MenuItem("Open Codex in Microsoft Store to check", OpenMicrosoftStore);
        storeUpdateItem.Visible = false;
        copyDiagnosticsItem = MenuItem("Copy redacted diagnostics", CopyDiagnostics);
        recentActivityItem = MenuItem(
            "Recent Continuity activity\u2026",
            OpenRecentActivity);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            healthItem,
            agentsItem,
            updateItem,
            updateDetailItem,
            versionDetailItem,
            readinessItem,
            applyDetailItem,
            activationScheduleItem,
            automaticApplyItem,
            retryApplyItem,
            snoozeMenu,
            clearSnoozeItem,
            activationWindowMenu,
            clearActivationWindowItem,
            recoveryItem,
            rollbackItem,
            new ToolStripSeparator(),
            MenuItem("Refresh now", async () => await RefreshAsync()),
            checkForUpdatesItem,
            releaseNotesItem,
            storeUpdateItem,
            MenuItem("Open diagnostics folder", OpenDiagnostics),
            copyDiagnosticsItem,
            recentActivityItem,
            MenuItem("Visit product site", OpenProductSite),
            new ToolStripSeparator(),
            MenuItem("Exit tray (agents keep running)", ExitTray),
        ]);
        notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = applicationIcon,
            Text = "Codex Continuity — checking backend",
            Visible = true,
        };
        menuHost = new TrayMenuHost(menu);
        notifyIcon.DoubleClick += (_, _) => _ = RefreshAsync();
        notifyIcon.BalloonTipClicked += HandleBalloonTipClicked;
        notifyIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                menuHost.Toggle(Cursor.Position);
            }
        };

        refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = checked((int)RefreshInterval.TotalMilliseconds),
            Enabled = true,
        };
        refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        _ = RefreshAsync();
    }

    private static ToolStripMenuItem MenuItem(string text, Action action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => _ = RunMenuActionAsync(() =>
        {
            action();
            return Task.CompletedTask;
        });
        return item;
    }

    private static ToolStripMenuItem MenuItem(string text, Func<Task> action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => _ = RunMenuActionAsync(action);
        return item;
    }

    private static async Task RunMenuActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or Win32Exception)
        {
            // The mutation presenter owns user-facing command errors. This guard
            // keeps a shell/UI event from becoming an unobserved async-void fault.
        }
    }

    private async Task RefreshAsync()
    {
        if (refreshInProgress)
        {
            return;
        }
        refreshInProgress = true;
        try
        {
            var status = await statusClient.ReadAsync(shutdown.Token);
            healthItem.Text = status.Detail;
            var activeAgents = status.ActiveAgentCount?.ToString() ?? "unknown";
            agentsItem.Text = $"Active agents: {activeAgents}";
            notifyIcon.Icon = TrayStatusPresentation.IconForHealth(
                status.Health,
                applicationIcon);
            var state = status.Health.ToString().ToLowerInvariant();
            notifyIcon.Text = $"Codex Continuity — {state} — {activeAgents} active agents";
            recoveryItem.Visible = TrayStatusPresentation.ShowRecovery(status.Health);
            var update = await statusClient.ReadUpdateAsync(shutdown.Token);
            updateItem.Text = TrayStatusPresentation.UpdateCounts(update);
            updateItem.Enabled = false;
            updateDetailItem.Text = TrayStatusPresentation.UpdateDetail(update, status.Health);
            versionDetailItem.Text = TrayStatusPresentation.VersionDetail(update);
            readinessItem.Text = TrayStatusPresentation.UpdaterReadinessDetail(update);
            latestReleaseUrl = update.LatestReleaseUrl;
            releaseNotesItem.Text = update.LatestVersion is { } latestVersion
                ? $"View release notes for v{latestVersion}"
                : "View release notes";
            releaseNotesItem.Visible = latestReleaseUrl is not null;
            rollbackItem.Text = update.RollbackVersion is { } rollbackVersion
                ? $"Rollback to v{rollbackVersion} on next safe start"
                : "Rollback Continuity on next safe start";
            rollbackItem.Visible = TrayStatusPresentation.ShowRollback(update);
            var apply = await statusClient.ReadApplyAsync(shutdown.Token);
            applyDetailItem.Text = TrayStatusPresentation.ApplyDetail(apply);
            activationScheduleItem.Text = TrayStatusPresentation.ActivationScheduleDetail(
                apply,
                DateTimeOffset.UtcNow);
            automaticApplyItem.Checked = apply.AutomaticApplyWhenIdle;
            applyPolicyMutable = TrayStatusPresentation.CanChangeApplyPolicy(apply);
            applyRetryAvailable = TrayStatusPresentation.ShowApplyRetry(apply);
            automaticApplyItem.Enabled = applyPolicyMutable;
            retryApplyItem.Visible = applyRetryAvailable;
            retryApplyItem.Enabled = applyRetryAvailable;
            snoozeMenu.Text = apply.SnoozedUntilUtc is { }
                ? "Change activation snooze"
                : "Snooze activation";
            snoozeMenu.Enabled = applyPolicyMutable;
            clearSnoozeItem.Visible = apply.SnoozedUntilUtc is not null;
            clearSnoozeItem.Enabled = applyPolicyMutable;
            activationWindowMenu.Text = apply.ActivationWindow is { }
                ? "Change activation window"
                : "Set activation window";
            activationWindowMenu.Enabled = applyPolicyMutable;
            customActivationWindowItem.Enabled = applyPolicyMutable;
            clearActivationWindowItem.Visible = apply.ActivationWindow is not null;
            clearActivationWindowItem.Enabled = applyPolicyMutable;
            var desktop = await statusClient.ReadDesktopUpdateAsync(shutdown.Token);
            storeUpdateItem.Visible = TrayStatusPresentation.ShowMicrosoftStoreUpdate(desktop);
            storeUpdateItem.Enabled = storeUpdateItem.Visible;
            if (desktop.AdvertisedVersion is { } advertisedVersion)
            {
                storeUpdateItem.Text = $"Open Codex v{advertisedVersion} in Microsoft Store to check";
            }

            var notificationSnapshot = TrayNotificationSnapshot.From(status, update, apply);
            var notification = TrayNotificationPlanner.Plan(
                previousNotificationSnapshot,
                notificationSnapshot,
                latestReleaseUrl);
            var activity = TrayActivityPlanner.Plan(
                previousNotificationSnapshot,
                notificationSnapshot);
            if (activity is not null)
            {
                _ = activityHistory.TryAppend(activity, DateTimeOffset.UtcNow);
            }
            previousNotificationSnapshot = notificationSnapshot;
            if (notification is not null && notificationDeduper.ShouldShow(notification))
            {
                ShowNotification(notification);
            }
            lastStatus = status;
            lastUpdate = update;
            lastApply = apply;
            lastDesktopUpdate = desktop;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or InvalidOperationException or Win32Exception)
        {
            healthItem.Text = $"Tray refresh failed: {Compact(exception.Message)}";
        }
        finally
        {
            refreshInProgress = false;
        }
    }

    private Task CheckForUpdatesAsync() => RunMutationAsync(
        updateDetailItem,
        "Checking for verified releases…",
        "Update check",
        statusClient.CheckForUpdatesAsync,
        successfulActivity: UserMutationActivity(
            "update",
            TrayActivityCatalog.UpdateCheckSummary));

    private Task ToggleAutomaticApplyAsync()
    {
        var enable = !automaticApplyItem.Checked;
        return RunMutationAsync(
            applyDetailItem,
            enable
                ? "Enabling safe idle activation..."
                : "Keeping updates staged until you apply them...",
            enable ? "Enable automatic activation" : "Disable automatic activation",
            token => statusClient.SetAutomaticApplyAsync(enable, token),
            successfulActivity: UserMutationActivity(
                "succeeded",
                TrayActivityCatalog.AutomaticApplySummary));
    }

    private Task RetryAutomaticApplyAsync() => RunMutationAsync(
        applyDetailItem,
        "Rearming safe idle activation...",
        "Retry automatic activation",
        token => statusClient.SetAutomaticApplyAsync(enabled: true, token),
        successfulActivity: UserMutationActivity(
            "succeeded",
            TrayActivityCatalog.AutomaticApplySummary));

    private Task SnoozeAsync(int minutes) => RunMutationAsync(
        activationScheduleItem,
        $"Snoozing activation for {minutes / 60} hour(s)...",
        "Snooze activation",
        token => statusClient.SetSnoozeAsync(minutes, token),
        successfulActivity: UserMutationActivity(
            "succeeded",
            TrayActivityCatalog.SnoozeSummary));

    private Task ClearSnoozeAsync() => RunMutationAsync(
        activationScheduleItem,
        "Resuming activation now...",
        "Resume activation",
        statusClient.ClearSnoozeAsync,
        successfulActivity: UserMutationActivity(
            "succeeded",
            TrayActivityCatalog.SnoozeSummary));

    private Task SetDefaultActivationWindowAsync() => RunMutationAsync(
        activationScheduleItem,
        "Restricting activation to 22:00-07:00 local...",
        "Set activation window",
        statusClient.SetDefaultActivationWindowAsync,
        successfulActivity: UserMutationActivity(
            "succeeded",
            TrayActivityCatalog.ActivationWindowSummary));

    private Task ShowCustomActivationWindowAsync()
    {
        if (!applyPolicyMutable)
        {
            return Task.CompletedTask;
        }

        using var dialog = new TrayActivationWindowDialog(TimeZoneInfo.Local.Id);
        var result = dialog.ShowDialog(menuHost.Owner);
        if (TrayActivationWindowDialog.AcceptedSelection(result, dialog.Selection)
            is not { } selection)
        {
            return Task.CompletedTask;
        }

        return RunMutationAsync(
            activationScheduleItem,
            $"Restricting activation to {selection.Range} local...",
            "Set custom activation window",
            token => statusClient.SetActivationWindowAsync(
                selection.Range,
                selection.TimeZoneId,
                token),
            successfulActivity: UserMutationActivity(
                "succeeded",
                TrayActivityCatalog.ActivationWindowSummary));
    }

    private Task ClearActivationWindowAsync() => RunMutationAsync(
        activationScheduleItem,
        "Allowing activation at any local time...",
        "Clear activation window",
        statusClient.ClearActivationWindowAsync,
        successfulActivity: UserMutationActivity(
            "succeeded",
            TrayActivityCatalog.ActivationWindowSummary));

    private Task RestartSupervisorAsync() => RunMutationAsync(
        healthItem,
        "Starting Continuity backend…",
        "Backend recovery",
        statusClient.RestartSupervisorAsync,
        TimeSpan.FromSeconds(2),
        UserMutationActivity(
            "succeeded",
            TrayActivityCatalog.BackendRestartSummary));

    private Task RollbackAsync() => RunMutationAsync(
        updateDetailItem,
        "Selecting the previous verified build...",
        "Rollback selection",
        statusClient.RollbackAsync,
        successfulActivity: UserMutationActivity(
            "succeeded",
            TrayActivityCatalog.RollbackSelectionSummary));

    private static TrayActivityEvent UserMutationActivity(
        string state,
        string summary) => new(
            TrayActivityKind.UserMutationSucceeded,
            null,
            state,
            summary,
            Deduplicate: false);

    private Task RunMutationAsync(
        ToolStripMenuItem feedbackItem,
        string pendingText,
        string action,
        Func<CancellationToken, Task<TrayCommandResult>> command,
        TimeSpan? settleDelay = null,
        TrayActivityEvent? successfulActivity = null) => mutationPresenter.RunAsync(
            pendingText,
            action,
            command,
            shutdown.Token,
            enabled =>
            {
                checkForUpdatesItem.Enabled = enabled;
                recoveryItem.Enabled = enabled;
                rollbackItem.Enabled = enabled && rollbackItem.Visible;
                automaticApplyItem.Enabled = enabled && applyPolicyMutable;
                retryApplyItem.Enabled = enabled && applyRetryAvailable;
                snoozeMenu.Enabled = enabled && applyPolicyMutable;
                clearSnoozeItem.Enabled = enabled && applyPolicyMutable;
                activationWindowMenu.Enabled = enabled && applyPolicyMutable;
                customActivationWindowItem.Enabled = enabled && applyPolicyMutable;
                clearActivationWindowItem.Enabled = enabled && applyPolicyMutable;
            },
            text => feedbackItem.Text = text,
            RefreshAsync,
            settleDelay,
            successfulActivity is null
                ? null
                : () => _ = activityHistory.TryAppend(
                    successfulActivity,
                    DateTimeOffset.UtcNow));

    private static void OpenDiagnostics()
    {
        var path = TrayStatusClient.ResolveDiagnosticsDirectory();
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenRecentActivity()
    {
        using var dialog = new TrayActivityHistoryDialog(
            TrayActivityHistoryFormatter.Render(activityHistory.Load()));
        dialog.ShowDialog(menuHost.Owner);
    }

    private static void OpenProductSite() => OpenUrl(
        "https://continuity.alirezaafshan.com");

    private void OpenReleaseNotes()
    {
        if (latestReleaseUrl is not null)
        {
            OpenUrl(latestReleaseUrl);
        }
    }

    private static void OpenMicrosoftStore()
    {
        try
        {
            OpenUrl("ms-windows-store://pdp/?ProductId=9PLM9XGG6VKS");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            OpenUrl("https://apps.microsoft.com/detail/9PLM9XGG6VKS");
        }
    }

    private void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(TrayDiagnosticsFormatter.Format(
                lastStatus ?? TrayStatusSnapshot.Unavailable("Status has not been read."),
                lastUpdate ?? ContinuityUpdateSnapshot.Unavailable(),
                lastApply ?? ContinuityApplySnapshot.Default,
                lastDesktopUpdate ?? TrayDesktopUpdateSnapshot.Unavailable()));
            readinessItem.Text = "Copied redacted diagnostics to clipboard";
        }
        catch (Exception exception) when (
            exception is ExternalException or InvalidOperationException or ThreadStateException)
        {
            readinessItem.Text = $"Copy diagnostics failed: {Compact(exception.Message)}";
        }
    }

    private void ShowNotification(TrayNotification notification)
    {
        notificationAction = notification.Action;
        notifyIcon.BalloonTipTitle = notification.Title;
        notifyIcon.BalloonTipText = notification.Body;
        notifyIcon.ShowBalloonTip(
            timeout: 5000,
            tipTitle: notification.Title,
            tipText: notification.Body,
            tipIcon: notification.Action == TrayNotificationAction.None
                ? ToolTipIcon.Info
                : ToolTipIcon.Warning);
    }

    private void HandleBalloonTipClicked(object? sender, EventArgs eventArgs)
    {
        var action = notificationAction;
        notificationAction = TrayNotificationAction.None;
        switch (action)
        {
            case TrayNotificationAction.OpenReleaseNotes:
                OpenReleaseNotes();
                break;
            case TrayNotificationAction.OpenDiagnostics:
                OpenDiagnostics();
                break;
            case TrayNotificationAction.RestartBackend:
                _ = RunMenuActionAsync(RestartSupervisorAsync);
                break;
        }
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static string Compact(string text)
    {
        const int maximumLength = 120;
        var singleLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= maximumLength
            ? singleLine
            : $"{singleLine[..maximumLength]}…";
    }

    private void ExitTray()
    {
        shutdown.Cancel();
        refreshTimer.Stop();
        menuHost.Close();
        notifyIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            shutdown.Cancel();
            refreshTimer.Dispose();
            menuHost.Dispose();
            notifyIcon.BalloonTipClicked -= HandleBalloonTipClicked;
            notifyIcon.Dispose();
            applicationIcon.Dispose();
            shutdown.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class TrayMutationPresenter
{
    private bool mutationInProgress;

    internal async Task RunAsync(
        string pendingText,
        string action,
        Func<CancellationToken, Task<TrayCommandResult>> command,
        CancellationToken cancellationToken,
        Action<bool> setActionsEnabled,
        Action<string> setFeedback,
        Func<Task> refreshAsync,
        TimeSpan? settleDelay = null,
        Action? onSuccess = null)
    {
        if (mutationInProgress)
        {
            return;
        }
        mutationInProgress = true;
        setActionsEnabled(false);
        setFeedback(pendingText);
        try
        {
            var result = await command(cancellationToken);
            if (result.ExitCode != 0)
            {
                setFeedback(TrayStatusPresentation.CommandFailure(action, result));
                return;
            }
            onSuccess?.Invoke();
            if (settleDelay is { } delay)
            {
                await Task.Delay(delay, cancellationToken);
            }
            await refreshAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or Win32Exception)
        {
            setFeedback(TrayStatusPresentation.CommandFailure(
                action,
                new TrayCommandResult(-1, string.Empty, exception.Message)));
        }
        finally
        {
            mutationInProgress = false;
            setActionsEnabled(true);
        }
    }
}
