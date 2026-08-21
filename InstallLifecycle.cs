using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexContinuity;

internal sealed record OwnedString(string? PreviousValue, string AppliedValue);

internal sealed record InstallState(
    int SchemaVersion,
    int Port,
    string InstalledExecutable,
    string? PreviousInstalledExecutable,
    string BinarySha256,
    OwnedString AppServerUrl,
    OwnedString UpdaterSetting,
    OwnedString StartupCommand,
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

    internal InstallOutcome Install(string sourceExecutable, int port)
    {
        LoopbackEndpoint.ValidatePort(port);
        var sourcePath = Path.GetFullPath(sourceExecutable);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Published Codex Continuity executable was not found.", sourcePath);
        }

        Directory.CreateDirectory(stateDirectory);
        var previousState = stateStore.Load();
        var hash = ComputeSha256(sourcePath);
        var installedExecutable = StageVersion(sourcePath, hash);
        var serverUrl = LoopbackEndpoint.WebSocketUrl(port);
        var startupCommand = StartupCommandBuilder.Build(installedExecutable, port);

        var previousUrl = platform.GetUserEnvironmentVariable(AppServerUrlVariable);
        var previousUpdaterSetting = platform.GetUserEnvironmentVariable(DisableUpdaterVariable);
        var previousStartup = platform.GetStartupCommand();
        var state = new InstallState(
            SchemaVersion: 1,
            Port: port,
            InstalledExecutable: installedExecutable,
            PreviousInstalledExecutable: PreviousExecutable(
                previousState,
                installedExecutable,
                stateDirectory),
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
            InstalledAtUtc: DateTimeOffset.UtcNow);

        try
        {
            platform.SetUserEnvironmentVariable(AppServerUrlVariable, state.AppServerUrl.AppliedValue);
            platform.SetUserEnvironmentVariable(
                DisableUpdaterVariable,
                state.UpdaterSetting.AppliedValue);
            platform.SetStartupCommand(state.StartupCommand.AppliedValue);
            stateStore.Save(state);
            platform.BroadcastEnvironmentChange();
        }
        catch
        {
            platform.SetUserEnvironmentVariable(AppServerUrlVariable, previousUrl);
            platform.SetUserEnvironmentVariable(DisableUpdaterVariable, previousUpdaterSetting);
            platform.SetStartupCommand(previousStartup);
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
        var currentStartup = platform.GetStartupCommand();
        var rolledBack = state with
        {
            InstalledExecutable = previousExecutable,
            PreviousInstalledExecutable = state.InstalledExecutable,
            BinarySha256 = ComputeSha256(previousExecutable),
            StartupCommand = CaptureOwnedValue(currentStartup, state.StartupCommand, startupCommand),
            InstalledAtUtc = DateTimeOffset.UtcNow,
        };
        platform.SetStartupCommand(startupCommand);
        stateStore.Save(rolledBack);
        return rolledBack;
    }

    private string StageVersion(string sourceExecutable, string hash)
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
        if (File.Exists(destination))
        {
            if (!string.Equals(ComputeSha256(destination), hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Staged executable hash mismatch at {destination}.");
            }
            return destination;
        }

        var temporaryPath = Path.Combine(versionDirectory, $"CodexContinuity-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourceExecutable, temporaryPath, overwrite: false);
            if (!string.Equals(ComputeSha256(temporaryPath), hash, StringComparison.OrdinalIgnoreCase))
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

    private static bool PathsEqual(string first, string second) =>
        Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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
}

internal sealed class WindowsInstallPlatform : IInstallPlatform
{
    private const string RunValueName = "CodexContinuity";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

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
