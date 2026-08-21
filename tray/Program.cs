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
    private readonly ToolStripMenuItem recoveryItem;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly TrayStatusClient statusClient;
    private readonly Icon healthyIcon;
    private bool refreshInProgress;

    internal ContinuityTrayContext()
    {
        var applicationDirectory = AppContext.BaseDirectory;
        var supervisorExecutable = TrayStatusClient.ResolveSupervisorExecutable(
            applicationDirectory);
        statusClient = new TrayStatusClient(supervisorExecutable);
        healthyIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? SystemIcons.Application;
        healthItem = new ToolStripMenuItem("Checking backend…") { Enabled = false };
        agentsItem = new ToolStripMenuItem("Active agents: checking…") { Enabled = false };
        updateItem = new ToolStripMenuItem("Updates: checking…") { Enabled = false };
        updateDetailItem = new ToolStripMenuItem("Update state: checking…") { Enabled = false };
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
            MenuItem("Check for updates now", async () => await CheckForUpdatesAsync()),
            MenuItem("Open diagnostics folder", OpenDiagnostics),
            MenuItem("Visit product site", OpenProductSite),
            new ToolStripSeparator(),
            MenuItem("Exit tray (agents keep running)", ExitTray),
        ]);
        notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = healthyIcon,
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
            agentsItem.Text = $"Active agents: {status.ActiveAgentCount}";
            notifyIcon.Icon = status.Health switch
            {
                ContinuityHealth.Healthy => healthyIcon,
                ContinuityHealth.Degraded => SystemIcons.Warning,
                ContinuityHealth.Unavailable => SystemIcons.Error,
                _ => throw new ArgumentOutOfRangeException(nameof(status), status.Health, null),
            };
            var state = status.Health.ToString().ToLowerInvariant();
            notifyIcon.Text = $"Codex Continuity — {state} — {status.ActiveAgentCount} active agents";
            recoveryItem.Visible = status.Health == ContinuityHealth.Unavailable;

            var update = await statusClient.ReadUpdateAsync(shutdown.Token);
            updateItem.Text = $"Updates: {update.ObservedCount} observed / " +
                $"{update.StagedCount} staged / {update.AppliedCount} active";
            updateItem.Enabled = update.LatestVersion is not null;
            updateItem.Click -= OpenLatestRelease;
            if (updateItem.Enabled)
            {
                updateItem.Click += OpenLatestRelease;
            }
            updateDetailItem.Text = UpdateDetail(update);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            refreshInProgress = false;
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            updateDetailItem.Text = "Checking for verified releases…";
            await statusClient.CheckForUpdatesAsync(shutdown.Token);
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RestartSupervisorAsync()
    {
        try
        {
            healthItem.Text = "Starting Continuity backend…";
            await statusClient.RestartSupervisorAsync(shutdown.Token);
            await Task.Delay(TimeSpan.FromSeconds(2), shutdown.Token);
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string UpdateDetail(ContinuityUpdateSnapshot update)
    {
        if (update.LastError is not null)
        {
            return $"Last update failed: {Compact(update.LastError)}";
        }
        if (update.RunningVersion is null)
        {
            return "Update tracking: waiting for first supervisor check";
        }
        return update.LatestVersion is null
            ? $"Running v{update.RunningVersion}; latest release unknown"
            : update.LatestState switch
            {
                "active" => $"Running v{update.RunningVersion}; latest is active",
                "staged" => $"v{update.LatestVersion} staged; running v{update.RunningVersion}",
                "failed" => $"v{update.LatestVersion} could not be staged",
                "observed" => $"v{update.LatestVersion} observed; staging pending",
                "unknown" => $"Running v{update.RunningVersion}; update state unknown",
                _ => $"Running v{update.RunningVersion}; update state {update.LatestState}",
            };
    }

    private static string Compact(string text)
    {
        const int maximumLength = 160;
        var singleLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= maximumLength
            ? singleLine
            : $"{singleLine[..maximumLength]}…";
    }

    private static void OpenDiagnostics()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YesterdaysLemon",
            "CodexContinuity");
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
            healthyIcon.Dispose();
            shutdown.Dispose();
        }
        base.Dispose(disposing);
    }
}
