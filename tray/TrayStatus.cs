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
    int? ActiveAgentCount,
    string Detail)
{
    internal static TrayStatusSnapshot Unavailable(string detail) =>
        new(ContinuityHealth.Unavailable, null, detail);
}

internal sealed record TrayCommandResult(int ExitCode, string Output, string Error);

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
    string? LastError)
{
    internal static ContinuityUpdateSnapshot Unavailable(string? error = null) =>
        new(null, false, null, 0, 0, 0, "unknown", error);
}

internal static class TrayStatusPresentation
{
    internal static bool ShowRecovery(ContinuityHealth health) => health == ContinuityHealth.Unavailable;

    internal static string UpdateCounts(ContinuityUpdateSnapshot update) =>
        $"Updates: {update.ObservedCount} observed / {update.StagedCount} staged / " +
        $"{update.AppliedCount} applied";

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
            "active" => $"{currentVersion}; latest is active",
            "staged" => $"{currentVersion}; v{update.LatestVersion} staged",
            "deferred" => $"{currentVersion}; v{update.LatestVersion} deferred by rollback",
            "inactive" => $"{currentVersion}; latest v{update.LatestVersion} is not active",
            "ahead" => $"{currentVersion}; ahead of stable v{update.LatestVersion}",
            "failed" => $"{currentVersion}; v{update.LatestVersion} could not be staged",
            "observed" => $"{currentVersion}; v{update.LatestVersion} observed; staging pending",
            "unknown" => $"{currentVersion}; update state unknown",
            _ => $"{currentVersion}; update state {update.LatestState}",
        };
    }

    internal static string CommandFailure(string action, TrayCommandResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        detail = string.IsNullOrWhiteSpace(detail) ? $"exit code {result.ExitCode}" : detail;
        return $"{action} failed: {Compact(detail)}";
    }

    private static string Compact(string text)
    {
        const int maximumLength = 160;
        var singleLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= maximumLength
            ? singleLine
            : $"{singleLine[..maximumLength]}…";
    }
}

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
        var health = supervisorState == "waitingForCodexExit"
            ? ContinuityHealth.Degraded
            : ready && supervisorState == "running"
                ? ContinuityHealth.Healthy
                : ready
                    ? ContinuityHealth.Degraded
                    : ContinuityHealth.Unavailable;
        var detail = health switch
        {
            ContinuityHealth.Healthy => "Backend ready",
            ContinuityHealth.Degraded when supervisorState == "waitingForCodexExit" =>
                "Armed; waiting for the current Codex desktop to close naturally",
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
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ContinuityUpdateSnapshot.Unavailable("Update status is invalid.");
        }
        try
        {
            return new ContinuityUpdateSnapshot(
                ReadString(root, "runningVersion"),
                ReadBool(root, "runningProcessObserved"),
                ReadString(root, "latestVersion"),
                ReadInt(root, "observedCount"),
                ReadInt(root, "stagedCount"),
                ReadInt(root, "appliedCount"),
                ReadString(root, "latestState") ?? "unknown",
                ReadString(root, "lastError"));
        }
        catch (InvalidOperationException)
        {
            return ContinuityUpdateSnapshot.Unavailable("Update status is invalid.");
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
}

internal sealed class TrayStatusClient(
    string supervisorExecutable,
    string? mutationApplicationDirectory = null,
    string? mutationStateDirectory = null,
    string? mutationLegacyStateDirectory = null,
    Func<string, IReadOnlyList<string>, CancellationToken, Task<TrayCommandResult>>?
        mutationProcessRunner = null)
{
    internal const int DefaultPort = 45123;

    private readonly string applicationDirectory =
        mutationApplicationDirectory ?? AppContext.BaseDirectory;
    private readonly string actionStateDirectory = mutationStateDirectory ?? StateDirectory;
    private readonly string actionLegacyStateDirectory =
        mutationLegacyStateDirectory ?? LegacyStateDirectory;
    private readonly TrayCommandGate mutationGate = new();
    private readonly Func<
        string,
        IReadOnlyList<string>,
        CancellationToken,
        Task<TrayCommandResult>> processRunner = mutationProcessRunner ?? RunProcessAsync;

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
        try
        {
            var result = await processRunner(
                supervisorExecutable,
                ["status", "--port", port.ToString()],
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Output))
            {
                var parsed = TrayStatusParser.Parse(result.Output);
                if (result.ExitCode == 0 ||
                    parsed.Detail.StartsWith("Armed;", StringComparison.Ordinal))
                {
                    return parsed;
                }
            }
            return TrayStatusSnapshot.Unavailable(
                string.IsNullOrWhiteSpace(result.Error)
                    ? "Backend unavailable"
                    : result.Error.Trim());
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or Win32Exception or JsonException)
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

    internal Task<TrayCommandResult> CheckForUpdatesAsync(CancellationToken cancellationToken) =>
        RunMutationAsync(_ => ["update"], cancellationToken);

    internal Task<TrayCommandResult> RestartSupervisorAsync(CancellationToken cancellationToken) =>
        RunMutationAsync(target => target.SelectedExecutable is null
            ? ["repair", "--start-now"]
            : [
                "repair",
                "--start-now",
                "--expected-installed-executable",
                target.SelectedExecutable,
                "--expected-installed-sha256",
                target.ExpectedSha256!,
            ], cancellationToken);

    internal static string ResolveSupervisorExecutable(string applicationDirectory)
        => ResolveSupervisorExecutable(applicationDirectory, StateDirectory);

    internal static string ResolveSupervisorExecutable(
        string applicationDirectory,
        string stateDirectory)
    {
        var stableExecutable = Path.Combine(
            stateDirectory,
            "bin",
            "CodexContinuity.exe");
        return File.Exists(stableExecutable)
            ? stableExecutable
            : Path.Combine(applicationDirectory, "CodexContinuity.exe");
    }

    internal static TrayMutationTarget ResolveMutationTarget(
        string applicationDirectory,
        string currentDirectory,
        string legacyDirectory)
    {
        var bundledExecutable = Path.GetFullPath(
            Path.Combine(applicationDirectory, "CodexContinuity.exe"));
        var statePath = new[] { currentDirectory, legacyDirectory }
            .Select(directory => Path.Combine(directory, "install-state.json"))
            .FirstOrDefault(File.Exists);
        if (statePath is null)
        {
            return File.Exists(bundledExecutable)
                ? new(bundledExecutable, null, null, null)
                : new(null, null, null, "No immutable Continuity command is available.");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            var root = document.RootElement;
            if (IsDeferredUninstall(root))
            {
                return new(
                    null,
                    null,
                    null,
                    "Continuity is pending deferred uninstall; actions are disabled.");
            }
            var installedExecutable = root.GetProperty("installedExecutable").GetString();
            var expectedSha256 = root.GetProperty("binarySha256").GetString();
            var stateDirectory = Path.GetDirectoryName(statePath)!;
            if (!string.IsNullOrWhiteSpace(installedExecutable) &&
                !string.IsNullOrWhiteSpace(expectedSha256))
            {
                var fullExecutable = Path.GetFullPath(installedExecutable);
                var versionsDirectory = Path.GetFullPath(
                    Path.Combine(stateDirectory, "versions")) + Path.DirectorySeparatorChar;
                if (fullExecutable.StartsWith(versionsDirectory, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(fullExecutable).Equals(
                        "CodexContinuity.exe",
                        StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(fullExecutable))
                {
                    return new(fullExecutable, fullExecutable, expectedSha256, null);
                }
                return File.Exists(bundledExecutable)
                    ? new(bundledExecutable, fullExecutable, expectedSha256, null)
                    : new(null, null, null, "The installed Continuity command is unavailable.");
            }
            return new(null, null, null, "Installed Continuity state is invalid; actions are disabled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or KeyNotFoundException or InvalidDataException or InvalidOperationException or
            ArgumentException or NotSupportedException)
        {
            return new(null, null, null, "Installed Continuity state is invalid; actions are disabled.");
        }
    }

    private static bool IsDeferredUninstall(JsonElement root)
    {
        if (!root.TryGetProperty("lifecycle", out var lifecycle))
        {
            return false;
        }
        if (lifecycle.ValueKind == JsonValueKind.Number &&
            lifecycle.TryGetInt32(out var numericLifecycle) &&
            numericLifecycle is 0 or 1)
        {
            return numericLifecycle == 1;
        }
        if (lifecycle.ValueKind == JsonValueKind.String)
        {
            return lifecycle.GetString() switch
            {
                "installed" or "Installed" => false,
                "deferredUninstall" or "DeferredUninstall" => true,
                _ => throw new InvalidDataException("Unknown install lifecycle."),
            };
        }
        throw new InvalidDataException("Unknown install lifecycle.");
    }

    internal static string ResolveDiagnosticsDirectory(
        string currentDirectory,
        string legacyDirectory)
    {
        static bool HasLiveSupervisor(string directory)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(
                    Path.Combine(directory, "supervisor-status.json")));
                var root = document.RootElement;
                using var process = Process.GetProcessById(
                    root.GetProperty("supervisorProcessId").GetInt32());
                return !process.HasExited && process.StartTime.ToUniversalTime() <=
                    root.GetProperty("updatedAtUtc").GetDateTimeOffset().UtcDateTime;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException or
                FormatException or Win32Exception)
            {
                return false;
            }
        }
        static bool HasState(string directory) => new[]
        {
            "install-state.json",
            "update-status.json",
            "supervisor-status.json",
            "app-server.log",
        }.Any(fileName => File.Exists(Path.Combine(directory, fileName)));

        var liveDirectory = HasLiveSupervisor(currentDirectory)
            ? currentDirectory
            : HasLiveSupervisor(legacyDirectory) ? legacyDirectory : null;
        return liveDirectory ?? (HasState(currentDirectory) || !HasState(legacyDirectory)
            ? currentDirectory : legacyDirectory);
    }

    internal static string ResolveDiagnosticsDirectory() => ResolveDiagnosticsDirectory(
        StateDirectory,
        LegacyStateDirectory);

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

    private Task<TrayCommandResult> RunMutationAsync(
        Func<TrayMutationTarget, IReadOnlyList<string>> arguments,
        CancellationToken cancellationToken) => mutationGate.RunAsync(async () =>
        {
            var target = ResolveMutationTarget(
                applicationDirectory,
                actionStateDirectory,
                actionLegacyStateDirectory);
            return target.Executable is null
                ? new TrayCommandResult(-1, string.Empty, target.Error ?? "Command unavailable.")
                : await processRunner(target.Executable, arguments(target), cancellationToken);
        }, cancellationToken);

    internal static async Task<TrayCommandResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
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
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
            return new TrayCommandResult(
                process.ExitCode,
                await outputTask,
                await errorTask);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }
                await process.WaitForExitAsync();
            }
            await Task.WhenAll(outputTask, errorTask);
            throw;
        }
    }
}
