using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexContinuity;

internal sealed record OwnedString(string? PreviousValue, string AppliedValue);

internal enum TrayInstallMode
{
    Enabled,
    Disabled,
}

internal enum ExistingEndpointOwnership
{
    NotReady,
    Managed,
    Legacy,
    Foreign,
}

internal sealed record InstalledAppRegistration(
    string DisplayName,
    string DisplayVersion,
    string Publisher,
    string InstallLocation,
    string DisplayIcon,
    string UninstallCommand,
    string QuietUninstallCommand,
    string ModifyCommand,
    string UrlInfoAbout,
    int EstimatedSizeKilobytes);

internal sealed record InstallState(
    int SchemaVersion,
    int Port,
    string InstalledExecutable,
    string? PreviousInstalledExecutable,
    string? InstalledTrayExecutable,
    string? PreviousInstalledTrayExecutable,
    string BinarySha256,
    OwnedString AppServerUrl,
    OwnedString UpdaterSetting,
    OwnedString? CommandPath,
    OwnedString StartupCommand,
    OwnedString? TrayStartupCommand,
    InstalledAppRegistration? PreviousInstalledAppRegistration,
    InstalledAppRegistration? InstalledAppRegistration,
    DateTimeOffset InstalledAtUtc);

internal sealed record InstallOutcome(
    InstallState State,
    bool StagedUpgrade,
    bool CurrentBackendUnchanged);

internal static class ContinuityPaths
{
    internal static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YesterdaysLemon",
        "CodexContinuity");

    internal static string LegacyOpenAiStateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenAI",
        "CodexContinuity");

    internal static string VersionsDirectory(string stateDirectory) =>
        Path.Combine(stateDirectory, "versions");

    internal static string CommandDirectory(string stateDirectory) =>
        Path.Combine(stateDirectory, "bin");

    internal static string CommandExecutable(string stateDirectory) =>
        Path.Combine(CommandDirectory(stateDirectory), "CodexContinuity.exe");

    internal static string InstallStateFile(string stateDirectory) =>
        Path.Combine(stateDirectory, "install-state.json");

    internal static string SupervisorStatusFile(string stateDirectory) =>
        Path.Combine(stateDirectory, "supervisor-status.json");

    internal static string AppServerLogFile(string stateDirectory) =>
        Path.Combine(stateDirectory, "app-server.log");
}

internal interface IInstallPlatform
{
    string? GetUserEnvironmentVariable(string name);
    void SetUserEnvironmentVariable(string name, string? value);
    string? GetStartupCommand();
    void SetStartupCommand(string? value);
    string? GetTrayStartupCommand();
    void SetTrayStartupCommand(string? value);
    InstalledAppRegistration? GetInstalledAppRegistration();
    void SetInstalledAppRegistration(InstalledAppRegistration? registration);
    string? GetCleanupCommand();
    void SetCleanupCommand(string? value);
    void BroadcastEnvironmentChange();
}

internal sealed class InstallStateStore(string path)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal InstallState? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<InstallState>(File.ReadAllText(path), SerializerOptions)
            ?? throw new InvalidDataException($"Install state at {path} is empty or invalid.");
    }

    internal void Save(InstallState state)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Install state path has no directory: {path}");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, SerializerOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal void Delete()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

internal sealed class InstallCoordinator(
    string stateDirectory,
    IInstallPlatform platform,
    InstallStateStore stateStore,
    string? legacyStateDirectory = null)
{
    internal const string AppServerUrlVariable = "CODEX_APP_SERVER_WS_URL";
    internal const string DisableUpdaterVariable = "CODEX_SPARKLE_ENABLED";
    internal const string PathVariable = "Path";

    internal int? DetectLegacyInstalledPort()
    {
        if (stateStore.Load() is not null)
        {
            return null;
        }
        return LoadLegacyState()?.Port ?? LegacyInstalledPort(platform.GetStartupCommand());
    }

    internal InstallOutcome Install(
        string sourceExecutable,
        int port,
        TrayInstallMode trayInstallMode,
        ExistingEndpointOwnership endpointOwnership = ExistingEndpointOwnership.NotReady)
    {
        if (endpointOwnership == ExistingEndpointOwnership.Foreign)
        {
            throw new InvalidOperationException(
                "The requested port is already ready but is not owned by this Continuity installation. No configuration was changed.");
        }
        LoopbackEndpoint.ValidatePort(port);
        var sourcePath = Path.GetFullPath(sourceExecutable);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Published Codex Continuity executable was not found.", sourcePath);
        }

        Directory.CreateDirectory(stateDirectory);
        var previousState = stateStore.Load() ?? LoadLegacyState();
        var sourceTrayExecutable = Path.Combine(
            Path.GetDirectoryName(sourcePath) ?? string.Empty,
            "CodexContinuity.Tray.exe");
        if (trayInstallMode == TrayInstallMode.Enabled && !File.Exists(sourceTrayExecutable))
        {
            throw new FileNotFoundException(
                "The optional tray executable is missing. Use the complete release bundle or install with --no-tray.",
                sourceTrayExecutable);
        }

        var hash = ComputeSha256(sourcePath);
        var stagedVersion = StageVersion(
            sourcePath,
            trayInstallMode == TrayInstallMode.Enabled ? sourceTrayExecutable : null,
            hash);
        var installedExecutable = stagedVersion.SupervisorExecutable;
        var commandExecutable = PublishCommandExecutable(
            sourcePath,
            trayInstallMode == TrayInstallMode.Enabled ? sourceTrayExecutable : null,
            hash);
        var serverUrl = LoopbackEndpoint.WebSocketUrl(port);
        var startupCommand = StartupCommandBuilder.Build(installedExecutable, port);
        var trayStartupCommand = stagedVersion.TrayExecutable is null
            ? null
            : StartupCommandBuilder.BuildTray(stagedVersion.TrayExecutable);

        var previousUrl = platform.GetUserEnvironmentVariable(AppServerUrlVariable);
        var previousUpdaterSetting = platform.GetUserEnvironmentVariable(DisableUpdaterVariable);
        var previousPath = platform.GetUserEnvironmentVariable(PathVariable);
        var previousStartup = platform.GetStartupCommand();
        var previousTrayStartup = platform.GetTrayStartupCommand();
        var previousRegistration = platform.GetInstalledAppRegistration();
        var previousCleanupCommand = platform.GetCleanupCommand();
        var legacyInstalledPort = LegacyInstalledPort(previousStartup);
        var previousUrlToRestore = legacyInstalledPort is not null && string.Equals(
            previousUrl,
            LoopbackEndpoint.WebSocketUrl(legacyInstalledPort.Value),
            StringComparison.Ordinal)
                ? null
                : previousUrl;
        var previousUpdaterSettingToRestore = legacyInstalledPort is not null && string.Equals(
            previousUpdaterSetting,
            "false",
            StringComparison.Ordinal)
                ? null
                : previousUpdaterSetting;
        var previousStartupToRestore = legacyInstalledPort is null ? previousStartup : null;
        var commandPath = CaptureOwnedPath(
            previousPath,
            previousState?.CommandPath,
            ContinuityPaths.CommandDirectory(stateDirectory));
        var registration = BuildInstalledAppRegistration(
            commandExecutable,
            installedExecutable,
            stagedVersion.TrayExecutable);
        var state = new InstallState(
            SchemaVersion: 4,
            Port: port,
            InstalledExecutable: installedExecutable,
            PreviousInstalledExecutable: PreviousExecutable(
                previousState,
                installedExecutable,
                stateDirectory),
            InstalledTrayExecutable: stagedVersion.TrayExecutable,
            PreviousInstalledTrayExecutable: PreviousTrayExecutable(
                previousState,
                stagedVersion.TrayExecutable),
            BinarySha256: hash,
            AppServerUrl: CaptureOwnedValue(
                previousUrlToRestore,
                previousState?.AppServerUrl,
                serverUrl),
            UpdaterSetting: CaptureOwnedValue(
                previousUpdaterSettingToRestore,
                previousState?.UpdaterSetting,
                "false"),
            CommandPath: commandPath,
            StartupCommand: CaptureOwnedValue(
                previousStartupToRestore,
                previousState?.StartupCommand,
                startupCommand),
            TrayStartupCommand: trayStartupCommand is null
                ? null
                : CaptureOwnedValue(
                    previousTrayStartup,
                    previousState?.TrayStartupCommand,
                    trayStartupCommand),
            PreviousInstalledAppRegistration: previousState is not null &&
                Equals(previousRegistration, previousState.InstalledAppRegistration)
                ? previousState.PreviousInstalledAppRegistration
                : previousRegistration,
            InstalledAppRegistration: registration,
            InstalledAtUtc: DateTimeOffset.UtcNow);

        try
        {
            platform.SetCleanupCommand(MigrationCleanupCommand());
            platform.SetUserEnvironmentVariable(AppServerUrlVariable, state.AppServerUrl.AppliedValue);
            platform.SetUserEnvironmentVariable(
                DisableUpdaterVariable,
                state.UpdaterSetting.AppliedValue);
            if (state.CommandPath is not null)
            {
                platform.SetUserEnvironmentVariable(PathVariable, state.CommandPath.AppliedValue);
            }
            platform.SetStartupCommand(state.StartupCommand.AppliedValue);
            if (state.TrayStartupCommand is not null)
            {
                platform.SetTrayStartupCommand(state.TrayStartupCommand.AppliedValue);
            }
            else if (previousState?.TrayStartupCommand is not null &&
                     string.Equals(
                         previousTrayStartup,
                         previousState.TrayStartupCommand.AppliedValue,
                         StringComparison.Ordinal))
            {
                platform.SetTrayStartupCommand(previousState.TrayStartupCommand.PreviousValue);
            }
            platform.SetInstalledAppRegistration(registration);
            stateStore.Save(state);
            platform.BroadcastEnvironmentChange();
        }
        catch
        {
            platform.SetCleanupCommand(previousCleanupCommand);
            platform.SetUserEnvironmentVariable(AppServerUrlVariable, previousUrl);
            platform.SetUserEnvironmentVariable(DisableUpdaterVariable, previousUpdaterSetting);
            if (state.CommandPath is not null)
            {
                platform.SetUserEnvironmentVariable(PathVariable, previousPath);
            }
            platform.SetStartupCommand(previousStartup);
            platform.SetTrayStartupCommand(previousTrayStartup);
            platform.SetInstalledAppRegistration(previousRegistration);
            platform.BroadcastEnvironmentChange();
            throw;
        }

        return new InstallOutcome(
            state,
            StagedUpgrade: previousState is not null &&
                !PathsEqual(previousState.InstalledExecutable, installedExecutable),
            CurrentBackendUnchanged: true);
    }

    internal bool Uninstall()
    {
        var state = stateStore.Load() ?? LoadLegacyState();
        if (state is null)
        {
            return UninstallLegacyConfiguration();
        }

        RestoreOwnedEnvironmentValue(AppServerUrlVariable, state.AppServerUrl);
        RestoreOwnedEnvironmentValue(DisableUpdaterVariable, state.UpdaterSetting);
        if (state.CommandPath is not null)
        {
            RestoreOwnedEnvironmentValue(PathVariable, state.CommandPath);
        }
        if (string.Equals(
                platform.GetStartupCommand(),
                state.StartupCommand.AppliedValue,
                StringComparison.Ordinal))
        {
            platform.SetStartupCommand(state.StartupCommand.PreviousValue);
        }
        if (state.TrayStartupCommand is not null && string.Equals(
                platform.GetTrayStartupCommand(),
                state.TrayStartupCommand.AppliedValue,
                StringComparison.Ordinal))
        {
            platform.SetTrayStartupCommand(state.TrayStartupCommand.PreviousValue);
        }
        if (Equals(platform.GetInstalledAppRegistration(), state.InstalledAppRegistration))
        {
            platform.SetInstalledAppRegistration(state.PreviousInstalledAppRegistration);
        }

        platform.SetCleanupCommand(DeferredCleanupCommandBuilder.Build(UninstallCleanupDirectories()));
        stateStore.Delete();
        LegacyStateStore()?.Delete();
        platform.BroadcastEnvironmentChange();
        return true;
    }

    internal InstallState Rollback()
    {
        var state = (stateStore.Load() ?? LoadLegacyState())
            ?? throw new InvalidOperationException("No installed Continuity state is available.");
        var previousExecutable = state.PreviousInstalledExecutable;
        if (string.IsNullOrWhiteSpace(previousExecutable) || !File.Exists(previousExecutable))
        {
            throw new InvalidOperationException("No previous known-good Continuity build is available.");
        }

        var startupCommand = StartupCommandBuilder.Build(previousExecutable, state.Port);
        var previousTrayExecutable = state.PreviousInstalledTrayExecutable;
        var trayStartupCommand = previousTrayExecutable is null
            ? null
            : StartupCommandBuilder.BuildTray(previousTrayExecutable);
        var currentStartup = platform.GetStartupCommand();
        var currentTrayStartup = platform.GetTrayStartupCommand();
        var currentRegistration = platform.GetInstalledAppRegistration();
        var commandExecutable = ContinuityPaths.CommandExecutable(stateDirectory);
        var rolledBackRegistration = IsLegacyExecutable(previousExecutable)
            ? state.PreviousInstalledAppRegistration
            : BuildInstalledAppRegistration(
                File.Exists(commandExecutable) ? commandExecutable : previousExecutable,
                previousExecutable,
                previousTrayExecutable);
        var rolledBack = state with
        {
            InstalledExecutable = previousExecutable,
            PreviousInstalledExecutable = state.InstalledExecutable,
            InstalledTrayExecutable = previousTrayExecutable,
            PreviousInstalledTrayExecutable = state.InstalledTrayExecutable,
            BinarySha256 = ComputeSha256(previousExecutable),
            StartupCommand = CaptureOwnedValue(currentStartup, state.StartupCommand, startupCommand),
            TrayStartupCommand = trayStartupCommand is null
                ? null
                : CaptureOwnedValue(
                    currentTrayStartup,
                    state.TrayStartupCommand,
                    trayStartupCommand),
            InstalledAppRegistration = rolledBackRegistration,
            InstalledAtUtc = DateTimeOffset.UtcNow,
        };
        try
        {
            platform.SetStartupCommand(startupCommand);
            if (rolledBack.TrayStartupCommand is not null)
            {
                platform.SetTrayStartupCommand(rolledBack.TrayStartupCommand.AppliedValue);
            }
            else if (state.TrayStartupCommand is not null && string.Equals(
                         currentTrayStartup,
                         state.TrayStartupCommand.AppliedValue,
                         StringComparison.Ordinal))
            {
                platform.SetTrayStartupCommand(state.TrayStartupCommand.PreviousValue);
            }
            platform.SetInstalledAppRegistration(rolledBackRegistration);
            stateStore.Save(rolledBack);
        }
        catch
        {
            platform.SetStartupCommand(currentStartup);
            platform.SetTrayStartupCommand(currentTrayStartup);
            platform.SetInstalledAppRegistration(currentRegistration);
            throw;
        }
        return rolledBack;
    }

    private sealed record StagedVersion(string SupervisorExecutable, string? TrayExecutable);

    private string StageExecutable(string sourceExecutable, string destination)
    {
        var sourceHash = ComputeSha256(sourceExecutable);
        if (File.Exists(destination))
        {
            if (!string.Equals(
                    ComputeSha256(destination),
                    sourceHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Staged executable hash mismatch at {destination}.");
            }
            return destination;
        }

        var temporaryPath = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(sourceExecutable, temporaryPath, overwrite: false);
            if (!string.Equals(
                    ComputeSha256(temporaryPath),
                    sourceHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Staged executable failed its SHA-256 verification.");
            }
            File.Move(temporaryPath, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        return destination;
    }

    private StagedVersion StageVersion(
        string sourceExecutable,
        string? sourceTrayExecutable,
        string hash)
    {
        var assemblyVersion = typeof(InstallCoordinator).Assembly.GetName().Version;
        var version = assemblyVersion is null
            ? "dev"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        var versionDirectory = Path.Combine(
            ContinuityPaths.VersionsDirectory(stateDirectory),
            $"{version}-{hash[..12].ToLowerInvariant()}");
        Directory.CreateDirectory(versionDirectory);
        var destination = Path.Combine(versionDirectory, "CodexContinuity.exe");
        var supervisor = StageExecutable(sourceExecutable, destination);
        var tray = sourceTrayExecutable is null
            ? null
            : StageExecutable(
                sourceTrayExecutable,
                Path.Combine(versionDirectory, "CodexContinuity.Tray.exe"));
        return new StagedVersion(supervisor, tray);
    }

    private string PublishCommandExecutable(
        string sourceExecutable,
        string? sourceTrayExecutable,
        string hash)
    {
        var destination = ContinuityPaths.CommandExecutable(stateDirectory);
        PublishCommandFile(sourceExecutable, destination, hash);
        if (sourceTrayExecutable is not null)
        {
            PublishCommandFile(
                sourceTrayExecutable,
                Path.Combine(
                    ContinuityPaths.CommandDirectory(stateDirectory),
                    "CodexContinuity.Tray.exe"),
                ComputeSha256(sourceTrayExecutable));
        }
        return destination;
    }

    private static void PublishCommandFile(
        string sourceExecutable,
        string destination,
        string hash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (PathsEqual(sourceExecutable, destination) ||
            (File.Exists(destination) && string.Equals(
                ComputeSha256(destination),
                hash,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        var temporaryPath = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(sourceExecutable, temporaryPath, overwrite: false);
            if (!string.Equals(
                    ComputeSha256(temporaryPath),
                    hash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Published command failed its SHA-256 verification.");
            }
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private bool UninstallLegacyConfiguration()
    {
        var startup = platform.GetStartupCommand();
        var legacyPort = LegacyInstalledPort(startup);
        if (legacyPort is null)
        {
            return false;
        }

        platform.SetStartupCommand(null);
        var trayStartup = platform.GetTrayStartupCommand();
        if (trayStartup?.Contains("CodexContinuity.Tray", StringComparison.OrdinalIgnoreCase) == true)
        {
            platform.SetTrayStartupCommand(null);
        }
        var registration = platform.GetInstalledAppRegistration();
        if (registration?.UninstallCommand.Contains(
                "CodexContinuity",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            platform.SetInstalledAppRegistration(null);
        }
        var currentUrl = platform.GetUserEnvironmentVariable(AppServerUrlVariable);
        if (string.Equals(
                currentUrl,
                LoopbackEndpoint.WebSocketUrl(legacyPort.Value),
                StringComparison.Ordinal))
        {
            platform.SetUserEnvironmentVariable(AppServerUrlVariable, null);
        }
        if (string.Equals(
                platform.GetUserEnvironmentVariable(DisableUpdaterVariable),
                "false",
                StringComparison.Ordinal))
        {
            platform.SetUserEnvironmentVariable(DisableUpdaterVariable, null);
        }
        platform.SetCleanupCommand(DeferredCleanupCommandBuilder.Build(UninstallCleanupDirectories()));
        platform.BroadcastEnvironmentChange();
        return true;
    }

    private void RestoreOwnedEnvironmentValue(string name, OwnedString ownedValue)
    {
        if (string.Equals(
                platform.GetUserEnvironmentVariable(name),
                ownedValue.AppliedValue,
                StringComparison.Ordinal))
        {
            platform.SetUserEnvironmentVariable(name, ownedValue.PreviousValue);
        }
    }

    private static OwnedString CaptureOwnedValue(
        string? currentValue,
        OwnedString? previousOwnership,
        string appliedValue)
    {
        var valueToRestore = previousOwnership is not null &&
            string.Equals(
                currentValue,
                previousOwnership.AppliedValue,
                StringComparison.Ordinal)
                ? previousOwnership.PreviousValue
                : currentValue;
        return new OwnedString(valueToRestore, appliedValue);
    }

    private static OwnedString? CaptureOwnedPath(
        string? currentValue,
        OwnedString? previousOwnership,
        string commandDirectory)
    {
        if (previousOwnership is not null && string.Equals(
                currentValue,
                previousOwnership.AppliedValue,
                StringComparison.Ordinal))
        {
            return new OwnedString(
                previousOwnership.PreviousValue,
                AppendPath(previousOwnership.PreviousValue, commandDirectory));
        }
        if (PathContains(currentValue, commandDirectory))
        {
            return null;
        }

        return new OwnedString(currentValue, AppendPath(currentValue, commandDirectory));
    }

    private static string AppendPath(string? currentValue, string commandDirectory) =>
        PathContains(currentValue, commandDirectory)
            ? currentValue ?? commandDirectory
            : string.IsNullOrEmpty(currentValue)
                ? commandDirectory
                : $"{currentValue.TrimEnd(';')};{commandDirectory}";

    private static bool PathContains(string? pathValue, string directory)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return false;
        }

        var normalizedDirectory = NormalizePathEntry(directory);
        return pathValue.Split(';').Any(entry => string.Equals(
            NormalizePathEntry(entry),
            normalizedDirectory,
            StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePathEntry(string entry) => entry
        .Trim()
        .Trim('"')
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private string? PreviousExecutable(
        InstallState? previousState,
        string installedExecutable,
        string stateDirectory)
    {
        if (previousState is null)
        {
            return LegacyExecutableCandidates()
                .FirstOrDefault(executable =>
                    File.Exists(executable) && !PathsEqual(executable, installedExecutable));
        }
        return PathsEqual(previousState.InstalledExecutable, installedExecutable)
            ? previousState.PreviousInstalledExecutable
            : previousState.InstalledExecutable;
    }

    private static string? PreviousTrayExecutable(
        InstallState? previousState,
        string? installedTrayExecutable)
    {
        if (previousState is null)
        {
            return null;
        }
        if (installedTrayExecutable is not null &&
            previousState.InstalledTrayExecutable is not null &&
            PathsEqual(previousState.InstalledTrayExecutable, installedTrayExecutable))
        {
            return previousState.PreviousInstalledTrayExecutable;
        }
        return previousState.InstalledTrayExecutable;
    }

    private int? LegacyInstalledPort(string? startupCommand)
    {
        if (startupCommand is null)
        {
            return null;
        }

        foreach (var legacyExecutable in LegacyExecutableCandidates())
        {
            var escapedLegacyExecutable = legacyExecutable.Replace("'", "''", StringComparison.Ordinal);
            if (!startupCommand.Contains(escapedLegacyExecutable, StringComparison.OrdinalIgnoreCase) ||
                !startupCommand.Contains("serve", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            const string portMarker = "'--port','";
            var portStart = startupCommand.IndexOf(portMarker, StringComparison.OrdinalIgnoreCase);
            if (portStart < 0)
            {
                return null;
            }
            portStart += portMarker.Length;
            var portEnd = startupCommand.IndexOf('\'', portStart);
            return portEnd > portStart && int.TryParse(
                startupCommand.AsSpan(portStart, portEnd - portStart),
                out var port) &&
                port is >= 1 and <= IPEndPoint.MaxPort
                    ? port
                    : null;
        }
        return null;
    }

    private static bool PathsEqual(string first, string second) =>
        Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private bool IsLegacyExecutable(string executable) =>
        LegacyExecutableCandidates().Any(candidate => PathsEqual(executable, candidate));

    private IEnumerable<string> LegacyExecutableCandidates()
    {
        yield return Path.Combine(stateDirectory, "CodexContinuity.exe");
        if (legacyStateDirectory is not null && !PathsEqual(stateDirectory, legacyStateDirectory))
        {
            yield return Path.Combine(legacyStateDirectory, "CodexContinuity.exe");
        }
    }

    private InstallStateStore? LegacyStateStore() =>
        legacyStateDirectory is null || PathsEqual(stateDirectory, legacyStateDirectory)
            ? null
            : new InstallStateStore(ContinuityPaths.InstallStateFile(legacyStateDirectory));

    private InstallState? LoadLegacyState() => LegacyStateStore()?.Load();

    private string? MigrationCleanupCommand() =>
        legacyStateDirectory is not null &&
        !PathsEqual(stateDirectory, legacyStateDirectory) &&
        Directory.Exists(legacyStateDirectory)
            ? DeferredCleanupCommandBuilder.Build(legacyStateDirectory)
            : null;

    private IReadOnlyList<string> UninstallCleanupDirectories()
    {
        var directories = new List<string> { stateDirectory };
        if (legacyStateDirectory is not null &&
            !PathsEqual(stateDirectory, legacyStateDirectory) &&
            Directory.Exists(legacyStateDirectory))
        {
            directories.Add(legacyStateDirectory);
        }
        return directories;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private InstalledAppRegistration BuildInstalledAppRegistration(
        string commandExecutable,
        string installedExecutable,
        string? installedTrayExecutable)
    {
        var version = typeof(InstallCoordinator).Assembly.GetName().Version;
        var displayVersion = version is null
            ? "development"
            : $"{version.Major}.{version.Minor}.{version.Build}";
        var quotedExecutable = $"\"{commandExecutable}\"";
        return new InstalledAppRegistration(
            DisplayName: "Codex Continuity",
            DisplayVersion: displayVersion,
            Publisher: "YesterdaysLemon",
            InstallLocation: stateDirectory,
            DisplayIcon: $"{quotedExecutable},0",
            UninstallCommand: $"{quotedExecutable} uninstall",
            QuietUninstallCommand: $"{quotedExecutable} uninstall",
            ModifyCommand: $"{quotedExecutable} repair",
            UrlInfoAbout: "https://continuity.alirezaafshan.com",
            EstimatedSizeKilobytes: checked((int)Math.Max(
                1,
                (new FileInfo(commandExecutable).Length +
                 new FileInfo(installedExecutable).Length +
                 (installedTrayExecutable is not null && File.Exists(installedTrayExecutable)
                     ? new FileInfo(installedTrayExecutable).Length
                     : 0) +
                 1023) / 1024)));
    }
}

internal static class DeferredCleanupCommandBuilder
{
    internal static string Build(string stateDirectory) => Build([stateDirectory]);

    internal static string Build(IReadOnlyList<string> stateDirectories)
    {
        if (stateDirectories.Count == 0)
        {
            throw new ArgumentException("At least one cleanup directory is required.", nameof(stateDirectories));
        }

        var targets = stateDirectories
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var target in targets)
        {
            var root = Path.GetPathRoot(target);
            if (string.IsNullOrWhiteSpace(root) || string.Equals(
                    target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to schedule cleanup for a filesystem root.");
            }
        }

        var targetArray = string.Join(
            ", ",
            targets.Select(target =>
                $"'{target.Replace("'", "''", StringComparison.Ordinal)}'"));
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return $"\"{powershell}\" -NoProfile -WindowStyle Hidden -Command " +
               $"\"& {{ $targets = @({targetArray}); foreach ($target in $targets) {{ " +
               "for ($attempt = 0; $attempt -lt 20; $attempt++) { " +
               "try { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop; break } " +
               "catch { Start-Sleep -Milliseconds 500 } } } }\"";
    }
}

internal static class StartupCommandBuilder
{
    internal static string Build(string executable, int port)
    {
        var escapedExecutable = executable.Replace("'", "''", StringComparison.Ordinal);
        var escapedWorkingDirectory = (Path.GetDirectoryName(executable) ?? ContinuityPaths.StateDirectory)
            .Replace("'", "''", StringComparison.Ordinal);
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return $"\"{powershell}\" -NoProfile -WindowStyle Hidden -Command " +
               $"\"Start-Process -WindowStyle Hidden -FilePath '{escapedExecutable}' " +
               $"-WorkingDirectory '{escapedWorkingDirectory}' " +
               $"-ArgumentList 'serve','--port','{port}'\"";
    }

    internal static string BuildTray(string executable) => $"\"{executable}\"";
}

internal sealed class WindowsInstallPlatform : IInstallPlatform
{
    private const string RunValueName = "CodexContinuity";
    private const string TrayRunValueName = "CodexContinuityTray";
    private const string CleanupRunValueName = "!CodexContinuityCleanup";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexContinuity";

    public string? GetUserEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

    public void SetUserEnvironmentVariable(string name, string? value) =>
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);

    public string? GetStartupCommand()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return runKey?.GetValue(RunValueName) as string;
    }

    public void SetStartupCommand(string? value)
    {
        if (value is null)
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            runKey?.DeleteValue(RunValueName, throwOnMissingValue: false);
            return;
        }

        using var writableRunKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        writableRunKey.SetValue(RunValueName, value);
    }

    public string? GetTrayStartupCommand()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return runKey?.GetValue(TrayRunValueName) as string;
    }

    public void SetTrayStartupCommand(string? value)
    {
        if (value is null)
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            runKey?.DeleteValue(TrayRunValueName, throwOnMissingValue: false);
            return;
        }

        using var writableRunKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        writableRunKey.SetValue(TrayRunValueName, value);
    }

    public InstalledAppRegistration? GetInstalledAppRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        if (key?.GetValue("UninstallString") is not string uninstallCommand)
        {
            return null;
        }

        return new InstalledAppRegistration(
            DisplayName: key.GetValue("DisplayName") as string ?? "Codex Continuity",
            DisplayVersion: key.GetValue("DisplayVersion") as string ?? "unknown",
            Publisher: key.GetValue("Publisher") as string ?? "unknown",
            InstallLocation: key.GetValue("InstallLocation") as string ?? string.Empty,
            DisplayIcon: key.GetValue("DisplayIcon") as string ?? string.Empty,
            UninstallCommand: uninstallCommand,
            QuietUninstallCommand: key.GetValue("QuietUninstallString") as string ?? uninstallCommand,
            ModifyCommand: key.GetValue("ModifyPath") as string ?? string.Empty,
            UrlInfoAbout: key.GetValue("URLInfoAbout") as string ?? string.Empty,
            EstimatedSizeKilobytes: key.GetValue("EstimatedSize") is int estimatedSize
                ? estimatedSize
                : 0);
    }

    public void SetInstalledAppRegistration(InstalledAppRegistration? registration)
    {
        if (registration is null)
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        key.SetValue("DisplayName", registration.DisplayName);
        key.SetValue("DisplayVersion", registration.DisplayVersion);
        key.SetValue("Publisher", registration.Publisher);
        key.SetValue("InstallLocation", registration.InstallLocation);
        key.SetValue("DisplayIcon", registration.DisplayIcon);
        key.SetValue("UninstallString", registration.UninstallCommand);
        key.SetValue("QuietUninstallString", registration.QuietUninstallCommand);
        key.SetValue("ModifyPath", registration.ModifyCommand);
        key.SetValue("URLInfoAbout", registration.UrlInfoAbout);
        key.SetValue("EstimatedSize", registration.EstimatedSizeKilobytes, RegistryValueKind.DWord);
        key.SetValue("NoModify", 0, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 0, RegistryValueKind.DWord);
    }

    public string? GetCleanupCommand()
    {
        using var runOnceKey = Registry.CurrentUser.OpenSubKey(RunOnceKeyPath);
        return runOnceKey?.GetValue(CleanupRunValueName) as string;
    }

    public void SetCleanupCommand(string? value)
    {
        if (value is null)
        {
            using var runOnceKey = Registry.CurrentUser.OpenSubKey(RunOnceKeyPath, writable: true);
            runOnceKey?.DeleteValue(CleanupRunValueName, throwOnMissingValue: false);
            return;
        }

        using var writableRunOnceKey = Registry.CurrentUser.CreateSubKey(RunOnceKeyPath);
        writableRunOnceKey.SetValue(CleanupRunValueName, value);
    }

    public void BroadcastEnvironmentChange()
    {
        const uint wmSettingChange = 0x001A;
        const uint abortIfHung = 0x0002;
        _ = SendMessageTimeout(
            new IntPtr(0xffff),
            wmSettingChange,
            UIntPtr.Zero,
            "Environment",
            abortIfHung,
            5000,
            out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out UIntPtr result);
}
