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
    OwnedString StartupCommand,
    OwnedString? TrayStartupCommand,
    InstalledAppRegistration? PreviousInstalledAppRegistration,
    InstalledAppRegistration InstalledAppRegistration,
    DateTimeOffset InstalledAtUtc);

internal sealed record InstallOutcome(
    InstallState State,
    bool StagedUpgrade,
    bool CurrentBackendUnchanged);

internal static class ContinuityPaths
{
    internal static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenAI",
        "CodexContinuity");

    internal static string VersionsDirectory(string stateDirectory) =>
        Path.Combine(stateDirectory, "versions");

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
    InstallStateStore stateStore)
{
    internal const string AppServerUrlVariable = "CODEX_APP_SERVER_WS_URL";
    internal const string DisableUpdaterVariable = "CODEX_SPARKLE_ENABLED";

    internal InstallOutcome Install(
        string sourceExecutable,
        int port,
        TrayInstallMode trayInstallMode)
    {
        LoopbackEndpoint.ValidatePort(port);
        var sourcePath = Path.GetFullPath(sourceExecutable);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Published Codex Continuity executable was not found.", sourcePath);
        }

        Directory.CreateDirectory(stateDirectory);
        var previousState = stateStore.Load();
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
        var serverUrl = LoopbackEndpoint.WebSocketUrl(port);
        var startupCommand = StartupCommandBuilder.Build(installedExecutable, port);
        var trayStartupCommand = stagedVersion.TrayExecutable is null
            ? null
            : StartupCommandBuilder.BuildTray(stagedVersion.TrayExecutable);

        var previousUrl = platform.GetUserEnvironmentVariable(AppServerUrlVariable);
        var previousUpdaterSetting = platform.GetUserEnvironmentVariable(DisableUpdaterVariable);
        var previousStartup = platform.GetStartupCommand();
        var previousTrayStartup = platform.GetTrayStartupCommand();
        var previousRegistration = platform.GetInstalledAppRegistration();
        var registration = BuildInstalledAppRegistration(installedExecutable);
        var state = new InstallState(
            SchemaVersion: 3,
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
            AppServerUrl: CaptureOwnedValue(previousUrl, previousState?.AppServerUrl, serverUrl),
            UpdaterSetting: CaptureOwnedValue(
                previousUpdaterSetting,
                previousState?.UpdaterSetting,
                "false"),
            StartupCommand: CaptureOwnedValue(
                previousStartup,
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
            platform.SetUserEnvironmentVariable(AppServerUrlVariable, state.AppServerUrl.AppliedValue);
            platform.SetUserEnvironmentVariable(
                DisableUpdaterVariable,
                state.UpdaterSetting.AppliedValue);
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
            platform.SetUserEnvironmentVariable(AppServerUrlVariable, previousUrl);
            platform.SetUserEnvironmentVariable(DisableUpdaterVariable, previousUpdaterSetting);
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
        var state = stateStore.Load();
        if (state is null)
        {
            return UninstallLegacyConfiguration();
        }

        RestoreOwnedEnvironmentValue(AppServerUrlVariable, state.AppServerUrl);
        RestoreOwnedEnvironmentValue(DisableUpdaterVariable, state.UpdaterSetting);
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

        stateStore.Delete();
        platform.BroadcastEnvironmentChange();
        return true;
    }

    internal InstallState Rollback()
    {
        var state = stateStore.Load()
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
            InstalledAppRegistration = BuildInstalledAppRegistration(previousExecutable),
            InstalledAtUtc = DateTimeOffset.UtcNow,
        };
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
        platform.SetInstalledAppRegistration(rolledBack.InstalledAppRegistration);
        stateStore.Save(rolledBack);
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

    private bool UninstallLegacyConfiguration()
    {
        var startup = platform.GetStartupCommand();
        var ownedStartup = startup is not null &&
            startup.Contains("CodexContinuity", StringComparison.OrdinalIgnoreCase) &&
            startup.Contains("serve", StringComparison.OrdinalIgnoreCase);
        if (!ownedStartup)
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
        if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme == "ws" &&
            IPAddress.TryParse(uri.Host, out var address) &&
            IPAddress.IsLoopback(address))
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

    private static string? PreviousExecutable(
        InstallState? previousState,
        string installedExecutable,
        string stateDirectory)
    {
        if (previousState is null)
        {
            var legacyExecutable = Path.Combine(stateDirectory, "CodexContinuity.exe");
            return File.Exists(legacyExecutable) && !PathsEqual(legacyExecutable, installedExecutable)
                ? legacyExecutable
                : null;
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

    private static bool PathsEqual(string first, string second) =>
        Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private InstalledAppRegistration BuildInstalledAppRegistration(string executable)
    {
        var version = typeof(InstallCoordinator).Assembly.GetName().Version;
        var displayVersion = version is null
            ? "development"
            : $"{version.Major}.{version.Minor}.{version.Build}";
        var quotedExecutable = $"\"{executable}\"";
        return new InstalledAppRegistration(
            DisplayName: "Codex Continuity",
            DisplayVersion: displayVersion,
            Publisher: "YesterdaysLemon",
            InstallLocation: Path.GetDirectoryName(executable) ?? stateDirectory,
            DisplayIcon: $"{quotedExecutable},0",
            UninstallCommand: $"{quotedExecutable} uninstall",
            QuietUninstallCommand: $"{quotedExecutable} uninstall",
            ModifyCommand: $"{quotedExecutable} install",
            UrlInfoAbout: "https://codex-continuity.alirezaafshan4.chatgpt.site",
            EstimatedSizeKilobytes: checked((int)Math.Max(
                1,
                (new FileInfo(executable).Length +
                 (File.Exists(Path.Combine(
                     Path.GetDirectoryName(executable) ?? stateDirectory,
                     "CodexContinuity.Tray.exe"))
                     ? new FileInfo(Path.Combine(
                         Path.GetDirectoryName(executable) ?? stateDirectory,
                         "CodexContinuity.Tray.exe")).Length
                     : 0) +
                 1023) / 1024)));
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
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
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
