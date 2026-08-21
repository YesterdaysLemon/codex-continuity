using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace CodexContinuity.Tray;

internal enum ContinuityHealth
{
    Healthy,
    Degraded,
    Unavailable,
}

internal sealed record TrayStatusSnapshot(
    ContinuityHealth Health,
    int ActiveAgentCount,
    string Detail)
{
    internal static TrayStatusSnapshot Unavailable(string detail) =>
        new(ContinuityHealth.Unavailable, 0, detail);
}

internal sealed record ContinuityUpdate(bool Available, string? Version);

internal static class TrayStatusParser
{
    internal static TrayStatusSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var ready = root.TryGetProperty("ready", out var readyElement) && readyElement.GetBoolean();
        var activeAgentCount = root.TryGetProperty("activeThreadCount", out var countElement)
            ? countElement.GetInt32()
            : 0;
        var supervisorState = root.TryGetProperty("supervisor", out var supervisor) &&
            supervisor.ValueKind == JsonValueKind.Object &&
            supervisor.TryGetProperty("state", out var stateElement)
                ? stateElement.GetString()
                : null;
        var health = ready && supervisorState is "running" or "attached"
            ? ContinuityHealth.Healthy
            : ready
                ? ContinuityHealth.Degraded
                : ContinuityHealth.Unavailable;
        var detail = health switch
        {
            ContinuityHealth.Healthy => "Backend ready",
            ContinuityHealth.Degraded => $"Backend ready; supervisor {supervisorState ?? "state unknown"}",
            ContinuityHealth.Unavailable => "Backend unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(health), health, null),
        };
        return new TrayStatusSnapshot(health, activeAgentCount, detail);
    }
}

internal sealed class TrayStatusClient(string supervisorExecutable)
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
        DefaultRequestHeaders =
        {
            UserAgent = { new("CodexContinuity.Tray", "0.2") },
        },
    };

    internal async Task<TrayStatusSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(supervisorExecutable))
        {
            return TrayStatusSnapshot.Unavailable("Supervisor executable not found");
        }

        var port = ReadInstalledPort();
        var startInfo = new ProcessStartInfo(supervisorExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return TrayStatusSnapshot.Unavailable("Could not run the status probe");
            }
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            return process.ExitCode == 0
                ? TrayStatusParser.Parse(output)
                : TrayStatusSnapshot.Unavailable(
                    string.IsNullOrWhiteSpace(error) ? "Backend unavailable" : error.Trim());
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or JsonException)
        {
            return TrayStatusSnapshot.Unavailable(exception.Message);
        }
    }

    internal async Task<ContinuityUpdate> ReadUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync(
                "https://api.github.com/repos/YesterdaysLemon/codex-continuity/releases/latest",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var tag = document.RootElement.GetProperty("tag_name").GetString();
            var normalized = tag?.TrimStart('v', 'V');
            var current = Assembly.GetExecutingAssembly().GetName().Version;
            return Version.TryParse(normalized, out var latest) && current is not null
                ? new ContinuityUpdate(latest > current, tag)
                : new ContinuityUpdate(false, tag);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            return new ContinuityUpdate(false, null);
        }
    }

    private static int ReadInstalledPort()
    {
        var statePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "CodexContinuity",
            "install-state.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            return document.RootElement.TryGetProperty("port", out var port)
                ? port.GetInt32()
                : 45123;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return 45123;
        }
    }
}
