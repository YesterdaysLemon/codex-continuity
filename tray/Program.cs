using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;

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
    private readonly ToolStripMenuItem checkForUpdatesItem;
    private readonly ToolStripMenuItem recoveryItem;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly TrayStatusClient statusClient;
    private readonly Icon applicationIcon;
    private bool refreshInProgress;
    private readonly TrayMutationPresenter mutationPresenter = new();

    internal ContinuityTrayContext()
    {
        var applicationDirectory = AppContext.BaseDirectory;
        var supervisorExecutable = TrayStatusClient.ResolveSupervisorExecutable(
            applicationDirectory);
        statusClient = new TrayStatusClient(supervisorExecutable, applicationDirectory);
        applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? SystemIcons.Application;
        healthItem = new ToolStripMenuItem("Checking backend…") { Enabled = false };
        agentsItem = new ToolStripMenuItem("Active agents: checking…") { Enabled = false };
        updateItem = new ToolStripMenuItem("Updates: checking…") { Enabled = false };
        updateDetailItem = new ToolStripMenuItem("Update state: checking…") { Enabled = false };

        checkForUpdatesItem = MenuItem(
            "Check for updates now",
            async () => await CheckForUpdatesAsync());
        recoveryItem = MenuItem(
            "Restart Continuity backend",
            async () => await RestartSupervisorAsync());
        recoveryItem.Visible = false;

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            healthItem,
            agentsItem,
            updateItem,
            updateDetailItem,
            recoveryItem,
            new ToolStripSeparator(),
            MenuItem("Refresh now", async () => await RefreshAsync()),
            checkForUpdatesItem,
            MenuItem("Open diagnostics folder", OpenDiagnostics),
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
        notifyIcon.DoubleClick += async (_, _) => await RefreshAsync();
        notifyIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                menu.Show(Cursor.Position);
            }
        };

        refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = checked((int)RefreshInterval.TotalMilliseconds),
            Enabled = true,
        };
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _ = RefreshAsync();
    }

    private static ToolStripMenuItem MenuItem(string text, Action action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
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
            updateItem.Enabled = update.LatestVersion is not null;
            updateItem.Click -= OpenLatestRelease;
            if (updateItem.Enabled)
            {
                updateItem.Click += OpenLatestRelease;
            }
            updateDetailItem.Text = TrayStatusPresentation.UpdateDetail(update, status.Health);
        }
        catch (OperationCanceledException)
        {
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
        statusClient.CheckForUpdatesAsync);

    private Task RestartSupervisorAsync() => RunMutationAsync(
        healthItem,
        "Starting Continuity backend…",
        "Backend recovery",
        statusClient.RestartSupervisorAsync,
        TimeSpan.FromSeconds(2));

    private Task RunMutationAsync(
        ToolStripMenuItem feedbackItem,
        string pendingText,
        string action,
        Func<CancellationToken, Task<TrayCommandResult>> command,
        TimeSpan? settleDelay = null) => mutationPresenter.RunAsync(
            pendingText,
            action,
            command,
            shutdown.Token,
            enabled =>
            {
                checkForUpdatesItem.Enabled = enabled;
                recoveryItem.Enabled = enabled;
            },
            text => feedbackItem.Text = text,
            RefreshAsync,
            settleDelay);

    private static void OpenDiagnostics()
    {
        var path = TrayStatusClient.ResolveDiagnosticsDirectory();
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void OpenProductSite() => OpenUrl(
        "https://continuity.alirezaafshan.com");

    private static void OpenLatestRelease(object? sender, EventArgs eventArgs) => OpenUrl(
        "https://github.com/YesterdaysLemon/codex-continuity/releases/latest");

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private void ExitTray()
    {
        shutdown.Cancel();
        refreshTimer.Stop();
        notifyIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            shutdown.Cancel();
            refreshTimer.Dispose();
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
        TimeSpan? settleDelay = null)
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
