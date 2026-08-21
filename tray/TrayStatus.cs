using System.ComponentModel;
using System.Diagnostics;
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

internal sealed record ContinuityUpdateSnapshot(
    string? RunningVersion,
    string? LatestVersion,
    int ObservedCount,
    int StagedCount,
    int AppliedCount,
    string LatestState,
    string? LastError)
{
    internal static ContinuityUpdateSnapshot Unavailable(string? error = null) =>
        new(null, null, 0, 0, 0, "unknown", error);
}

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
        var health = ready && supervisorState == "running"
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

    internal static ContinuityUpdateSnapshot ParseUpdate(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new ContinuityUpdateSnapshot(
            ReadString(root, "runningVersion"),
            ReadString(root, "latestVersion"),
            ReadInt(root, "observedCount"),
            ReadInt(root, "stagedCount"),
            ReadInt(root, "appliedCount"),
            ReadString(root, "latestState") ?? "unknown",
            ReadString(root, "lastError"));
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;
}

internal sealed class TrayStatusClient(string supervisorExecutable)
{
    internal const int DefaultPort = 45123;

    private static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YesterdaysLemon",
        "CodexContinuity");

    private static string LegacyStateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenAI",
        "CodexContinuity");

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

    internal Task<ContinuityUpdateSnapshot> ReadUpdateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var path = ExistingStateFile("update-status.json");
            return Task.FromResult(path is null
                ? ContinuityUpdateSnapshot.Unavailable()
                : TrayStatusParser.ParseUpdate(File.ReadAllText(path)));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Task.FromResult(ContinuityUpdateSnapshot.Unavailable(exception.Message));
        }
    }

    internal async Task<bool> CheckForUpdatesAsync(CancellationToken cancellationToken) =>
        await RunCommandAsync(["update", "--port", ReadInstalledPort().ToString()], cancellationToken) == 0;

    internal async Task<bool> RestartSupervisorAsync(CancellationToken cancellationToken) =>
        await RunCommandAsync(["repair", "--start-now"], cancellationToken) == 0;

    internal static string ResolveSupervisorExecutable(string applicationDirectory)
    {
        var stableExecutable = Path.Combine(
            StateDirectory,
            "bin",
            "CodexContinuity.exe");
        return File.Exists(stableExecutable)
            ? stableExecutable
            : Path.Combine(applicationDirectory, "CodexContinuity.exe");
    }

    private static int ReadInstalledPort()
    {
        try
        {
            var statePath = ExistingStateFile("install-state.json");
            return statePath is null
                ? DefaultPort
                : ParseInstalledPort(File.ReadAllText(statePath));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return DefaultPort;
        }
    }

    internal static int ParseInstalledPort(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("port", out var portElement) ||
            portElement.ValueKind != JsonValueKind.Number ||
            !portElement.TryGetInt32(out var port) ||
            port is < 1 or > 65535)
        {
            return DefaultPort;
        }

        return port;
    }

    private static string? ExistingStateFile(string fileName)
    {
        var current = Path.Combine(StateDirectory, fileName);
        if (File.Exists(current))
        {
            return current;
        }
        var legacy = Path.Combine(LegacyStateDirectory, fileName);
        return File.Exists(legacy) ? legacy : null;
    }

    private async Task<int> RunCommandAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(supervisorExecutable))
        {
            return -1;
        }
        var startInfo = new ProcessStartInfo(supervisorExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return -1;
            }
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
            return process.ExitCode;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or Win32Exception)
        {
            return -1;
        }
    }
}
