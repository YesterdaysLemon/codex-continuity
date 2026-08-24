using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexContinuity.Tray;

internal sealed class TrayStatusClient(
    string supervisorExecutable,
    string? mutationApplicationDirectory = null,
    string? mutationStateDirectory = null,
    string? mutationLegacyStateDirectory = null,
    Func<string, IReadOnlyList<string>, CancellationToken, Task<TrayCommandResult>>?
        mutationProcessRunner = null)
{
    internal const int DefaultPort = 45123;
    private const int MaximumStateFileBytes = 1024 * 1024;
    private static readonly TimeSpan DesktopProbeCacheDuration = TimeSpan.FromHours(4);

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
    private TrayDesktopUpdateSnapshot? desktopUpdateCache;
    private DateTimeOffset desktopUpdateCacheAtUtc;

    internal string ActivityHistoryPath =>
        Path.Combine(ResolveOwningStateDirectory(), "tray-activity-history.json");

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
            var update = path is null
                ? ContinuityUpdateSnapshot.Unavailable()
                : TrayStatusParser.ParseUpdate(ReadBoundedText(path));
            return Task.FromResult(EnrichUpdateSnapshot(update));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or InvalidOperationException or FormatException)
        {
            return Task.FromResult(ContinuityUpdateSnapshot.Unavailable(exception.Message));
        }
    }

    internal async Task<TrayDesktopUpdateSnapshot> ReadDesktopUpdateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (desktopUpdateCache is not null &&
            DateTimeOffset.UtcNow - desktopUpdateCacheAtUtc < DesktopProbeCacheDuration)
        {
            return desktopUpdateCache;
        }
        if (!File.Exists(supervisorExecutable))
        {
            return TrayDesktopUpdateSnapshot.Unavailable("Supervisor executable not found.");
        }
        try
        {
            var result = await processRunner(
                supervisorExecutable,
                ["probe"],
                cancellationToken);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            {
                return TrayDesktopUpdateSnapshot.Unavailable(
                    string.IsNullOrWhiteSpace(result.Error)
                        ? "Codex Desktop update status could not be read."
                        : result.Error.Trim());
            }
            var parsed = TrayStatusParser.ParseDesktopUpdate(result.Output);
            desktopUpdateCache = parsed;
            desktopUpdateCacheAtUtc = DateTimeOffset.UtcNow;
            return parsed;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or InvalidOperationException or Win32Exception)
        {
            return TrayDesktopUpdateSnapshot.Unavailable(exception.Message);
        }
    }

    internal Task<ContinuityApplySnapshot> ReadApplyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var policyPath = ExistingStateFile("update-apply-policy.json");
            var statusPath = ExistingStateFile("update-apply-status.json");
            return Task.FromResult(TrayStatusParser.ParseApply(
                policyPath is null ? null : ReadBoundedText(policyPath),
                statusPath is null ? null : ReadBoundedText(statusPath)));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or InvalidOperationException or FormatException)
        {
            return Task.FromResult(ContinuityApplySnapshot.Unavailable(exception.Message));
        }
    }

    internal Task<TrayCommandResult> CheckForUpdatesAsync(CancellationToken cancellationToken) =>
        RunMutationAsync(_ => ["update"], cancellationToken);

    internal Task<TrayCommandResult> SetAutomaticApplyAsync(
        bool enabled,
        CancellationToken cancellationToken) => RunMutationAsync(
            _ => ["update-policy", enabled ? "--enable" : "--disable"],
            cancellationToken);

    internal Task<TrayCommandResult> SetSnoozeAsync(
        int minutes,
        CancellationToken cancellationToken)
    {
        if (minutes is < 1 or > 7 * 24 * 60)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes));
        }
        return RunMutationAsync(
            _ => [
                "update-policy",
                "--snooze-minutes",
                minutes.ToString(CultureInfo.InvariantCulture),
            ],
            cancellationToken);
    }

    internal Task<TrayCommandResult> ClearSnoozeAsync(
        CancellationToken cancellationToken) => RunMutationAsync(
            _ => ["update-policy", "--clear-snooze"],
            cancellationToken);

    internal Task<TrayCommandResult> SetDefaultActivationWindowAsync(
        CancellationToken cancellationToken) => RunMutationAsync(
            _ => ["update-policy", "--activation-window", "22:00-07:00"],
            cancellationToken);

    internal Task<TrayCommandResult> SetActivationWindowAsync(
        string range,
        string timeZoneId,
        CancellationToken cancellationToken)
    {
        if (!TrayActivationWindowPlanner.TryCreateRange(
                range,
                timeZoneId,
                out var selection,
                out var error))
        {
            throw new ArgumentException(error, nameof(range));
        }

        return RunMutationAsync(
            _ => TrayActivationWindowPlanner.BuildArguments(selection!),
            cancellationToken);
    }

    internal Task<TrayCommandResult> ClearActivationWindowAsync(
        CancellationToken cancellationToken) => RunMutationAsync(
            _ => ["update-policy", "--clear-activation-window"],
            cancellationToken);

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

    internal Task<TrayCommandResult> RollbackAsync(CancellationToken cancellationToken) =>
        RunMutationAsync(target => target.SelectedExecutable is null
            ? ["rollback"]
            : [
                "rollback",
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
            using var document = JsonDocument.Parse(ReadBoundedText(statePath));
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
                using var document = JsonDocument.Parse(ReadBoundedText(
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
            "update-apply-policy.json",
            "update-apply-status.json",
            "supervisor-handoff.json",
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

    private int ReadInstalledPort()
    {
        try
        {
            var statePath = ExistingStateFile("install-state.json");
            return statePath is null
                ? DefaultPort
                : ParseInstalledPort(ReadBoundedText(statePath));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or InvalidOperationException or FormatException)
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

    private string? ExistingStateFile(string fileName)
    {
        var current = Path.Combine(actionStateDirectory, fileName);
        if (File.Exists(current))
        {
            return current;
        }
        var legacy = Path.Combine(actionLegacyStateDirectory, fileName);
        return File.Exists(legacy) ? legacy : null;
    }

    private string ResolveOwningStateDirectory()
    {
        foreach (var fileName in new[]
        {
            "install-state.json",
            "update-status.json",
            "update-apply-policy.json",
            "update-apply-status.json",
            "supervisor-status.json",
        })
        {
            var path = ExistingStateFile(fileName);
            if (path is not null)
            {
                return Path.GetDirectoryName(path) ?? actionStateDirectory;
            }
        }
        return actionStateDirectory;
    }

    private ContinuityUpdateSnapshot EnrichUpdateSnapshot(ContinuityUpdateSnapshot update)
    {
        var selectedVersion = update.SelectedVersion;
        var rollbackVersion = update.RollbackVersion;
        TrayBuildIdentity? selectedBuild = update.SelectedBuild;
        TrayBuildIdentity? rollbackBuild = update.RollbackBuild;
        var installPath = ExistingStateFile("install-state.json");
        if (installPath is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(ReadBoundedText(installPath));
                var root = document.RootElement;
                var installedExecutable = ReadString(root, "installedExecutable");
                var installedSha256 = ReadString(root, "binarySha256");
                selectedBuild = BuildIdentity(
                    installedExecutable,
                    installedSha256,
                    selectedVersion,
                    "selected startup build");
                selectedVersion ??= selectedBuild.Version;
                var rollbackExecutable = ReadString(root, "previousInstalledExecutable");
                rollbackBuild = BuildIdentity(
                    rollbackExecutable,
                    expectedSha256: rollbackBuild?.Sha256,
                    rollbackVersion,
                    "rollback build");
                rollbackVersion ??= rollbackBuild.Version;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or
                    InvalidDataException or InvalidOperationException or FormatException)
            {
                selectedBuild ??= TrayBuildIdentity.Unknown(
                    "Installed state could not be read.");
                rollbackBuild ??= TrayBuildIdentity.Unknown(
                    "Installed state could not be read.");
            }
        }

        var runningBuild = ReadRunningBuild(update);
        return update with
        {
            SelectedVersion = selectedVersion,
            RollbackVersion = rollbackVersion,
            LatestReleaseUrl = TrayStatusParser.ReleaseUrl(update.LatestVersion),
            RunningBuild = runningBuild ?? update.RunningBuild,
            SelectedBuild = selectedBuild,
            RollbackBuild = rollbackBuild,
        };
    }

    private TrayBuildIdentity? ReadRunningBuild(ContinuityUpdateSnapshot update)
    {
        var path = ExistingStateFile("supervisor-status.json");
        if (path is null)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(ReadBoundedText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("supervisorExecutable", out var executableElement) ||
                executableElement.ValueKind != JsonValueKind.String ||
                executableElement.GetString() is not { } executable ||
                !root.TryGetProperty("supervisorProcessId", out var processElement) ||
                !processElement.TryGetInt32(out var processId) || processId <= 0)
            {
                return TrayBuildIdentity.Unknown("Running supervisor identity is unavailable.");
            }
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return TrayBuildIdentity.Unknown("The recorded supervisor process has exited.");
            }
            var liveExecutable = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(liveExecutable))
            {
                return TrayBuildIdentity.Unknown(
                    "The running supervisor executable could not be resolved.");
            }
            var fallbackVersion = update.RunningVersion;
            var identity = BuildIdentity(
                liveExecutable,
                update.RunningBuild?.Sha256,
                fallbackVersion,
                "running supervisor");
            return identity with { Proven = identity.Proven && update.RunningProcessObserved };
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or InvalidOperationException or ArgumentException or
                FormatException or Win32Exception)
        {
            return TrayBuildIdentity.Unknown("Running supervisor identity could not be verified.");
        }
    }

    private static TrayBuildIdentity BuildIdentity(
        string? executable,
        string? expectedSha256,
        string? fallbackVersion,
        string role)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return TrayBuildIdentity.Unknown($"No {role} is recorded.");
        }
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(executable);
        }
        catch (ArgumentException)
        {
            return TrayBuildIdentity.Unknown($"The {role} path is invalid.");
        }
        if (!File.Exists(fullPath))
        {
            return new(
                fallbackVersion,
                fullPath,
                expectedSha256,
                false,
                $"The {role} executable is missing.");
        }
        var actualSha256 = ComputeSha256(fullPath);
        var version = ReadFileVersion(fullPath) ?? fallbackVersion;
        var proven = expectedSha256 is not null &&
            actualSha256 is not null &&
            string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
        return new(
            version,
            fullPath,
            actualSha256,
            proven,
            proven
                ? null
                : expectedSha256 is null
                    ? $"No expected {role} digest is recorded."
                    : $"The {role} digest is not proven.");
    }

    private static string? ReadFileVersion(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return version.FileMajorPart == 0 && version.FileMinorPart == 0 &&
                version.FileBuildPart == 0
                    ? null
                    : $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }

    private static string? ComputeSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static string ReadBoundedText(string path, int maximumBytes = MaximumStateFileBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"State file exceeds the {maximumBytes}-byte tray read limit.");
        }
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
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
