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
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(4);
    private readonly CancellationTokenSource shutdown = new();
    private readonly NotifyIcon notifyIcon;
    private readonly ToolStripMenuItem healthItem;
    private readonly ToolStripMenuItem agentsItem;
    private readonly ToolStripMenuItem updateItem;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly TrayStatusClient statusClient;
    private readonly Icon healthyIcon;
    private bool refreshInProgress;
    private DateTimeOffset lastUpdateCheck = DateTimeOffset.MinValue;

    internal ContinuityTrayContext()
    {
        var applicationDirectory = AppContext.BaseDirectory;
        var supervisorExecutable = Path.Combine(applicationDirectory, "CodexContinuity.exe");
        statusClient = new TrayStatusClient(supervisorExecutable);
        healthyIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? SystemIcons.Application;
        healthItem = new ToolStripMenuItem("Checking backend…") { Enabled = false };
        agentsItem = new ToolStripMenuItem("Active agents: checking…") { Enabled = false };
        updateItem = new ToolStripMenuItem("Continuity update: checking…") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            healthItem,
            agentsItem,
            updateItem,
            new ToolStripSeparator(),
            MenuItem("Refresh now", async () => await RefreshAsync()),
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

            if (DateTimeOffset.UtcNow - lastUpdateCheck >= UpdateInterval)
            {
                var update = await statusClient.ReadUpdateAsync(shutdown.Token);
                updateItem.Text = update.Available
                    ? $"Continuity update available: {update.Version}"
                    : update.Version is null
                        ? "Continuity update: check unavailable"
                        : "Codex Continuity is up to date";
                updateItem.Enabled = update.Available;
                if (update.Available)
                {
                    updateItem.Click -= OpenLatestRelease;
                    updateItem.Click += OpenLatestRelease;
                }
                lastUpdateCheck = DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            refreshInProgress = false;
        }
    }

    private static void OpenDiagnostics()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
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
