using CodexContinuity;
using System.Diagnostics;
using System.Text;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class InstallCoordinatorTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-install-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(45123)]
    [InlineData(45124)]
    public void UninstallRestoresOwnedValuesWithoutRepeatingInstallPort(int installPort)
    {
        var platform = new FakeInstallPlatform
        {
            StartupCommand = "previous startup",
        };
        platform.Environment[InstallCoordinator.AppServerUrlVariable] = "ws://127.0.0.1:40000";
        platform.Environment[InstallCoordinator.DisableUpdaterVariable] = "true";
        platform.Environment[InstallCoordinator.PathVariable] = @"C:\UserTools";
        var coordinator = CreateCoordinator(platform);
        var source = CreateSource("version-one");

        var outcome = coordinator.Install(source, installPort, TrayInstallMode.Disabled);
        var removed = coordinator.Uninstall();

        Assert.True(removed);
        Assert.Equal(installPort, outcome.State.Port);
        Assert.Equal("ws://127.0.0.1:40000", platform.Environment[InstallCoordinator.AppServerUrlVariable]);
        Assert.Equal("true", platform.Environment[InstallCoordinator.DisableUpdaterVariable]);
        Assert.Equal(@"C:\UserTools", platform.Environment[InstallCoordinator.PathVariable]);
        Assert.Equal("previous startup", platform.StartupCommand);
        Assert.Null(platform.InstalledAppRegistration);
        Assert.Contains(Path.GetFullPath(root), ReadCleanupScript(platform.CleanupCommand));
    }

    [Fact]
    public void UninstallKeepsOwnedReconnectUrlUntilNextSignInWhenBackendIsStillRunning()
    {
        var platform = new FakeInstallPlatform
        {
            StartupCommand = "previous startup",
        };
        platform.Environment[InstallCoordinator.AppServerUrlVariable] = "ws://127.0.0.1:40000";
        platform.Environment[InstallCoordinator.DisableUpdaterVariable] = "true";
        var coordinator = CreateCoordinator(platform);
        coordinator.Install(CreateSource("version-one"), 45123, TrayInstallMode.Disabled);

        var removed = coordinator.Uninstall(
            UninstallReconnectPolicy.PreserveUntilNextSignIn);

        Assert.True(removed);
        Assert.Equal(
            LoopbackEndpoint.WebSocketUrl(45123),
            platform.Environment[InstallCoordinator.AppServerUrlVariable]);
        Assert.Equal("true", platform.Environment[InstallCoordinator.DisableUpdaterVariable]);
        Assert.Equal("previous startup", platform.StartupCommand);
        Assert.Null(platform.InstalledAppRegistration);
        Assert.True(File.Exists(ContinuityPaths.InstallStateFile(root)));
        Assert.Equal(
            InstallLifecycle.DeferredUninstall,
            new InstallStateStore(ContinuityPaths.InstallStateFile(root)).Load()!.Lifecycle);

        var cleanupScript = ReadCleanupScript(platform.CleanupCommand);
        Assert.Contains(InstallCoordinator.AppServerUrlVariable, cleanupScript);
        Assert.Contains(LoopbackEndpoint.WebSocketUrl(45123), cleanupScript);
        Assert.Contains("ws://127.0.0.1:40000", cleanupScript);
        Assert.Contains("[string]::Equals", cleanupScript);
        Assert.Contains(Path.GetFullPath(root), cleanupScript);
    }

    [Fact]
    public void ReinstallBeforeDeferredCleanupRetainsTheOriginalReconnectValue()
    {
        var platform = new FakeInstallPlatform();
        platform.Environment[InstallCoordinator.AppServerUrlVariable] = "ws://127.0.0.1:40000";
        var coordinator = CreateCoordinator(platform);
        coordinator.Install(CreateSource("version-one"), 45123, TrayInstallMode.Disabled);
        coordinator.Uninstall(UninstallReconnectPolicy.PreserveUntilNextSignIn);

        var reinstalled = coordinator.Install(
            CreateSource("version-two"),
            45123,
            TrayInstallMode.Disabled);
        coordinator.Uninstall();

        Assert.Equal(
            "ws://127.0.0.1:40000",
            reinstalled.State.AppServerUrl.PreviousValue);
        Assert.Equal(InstallLifecycle.Installed, reinstalled.State.Lifecycle);
        Assert.Equal(
            "ws://127.0.0.1:40000",
            platform.Environment[InstallCoordinator.AppServerUrlVariable]);
    }

    [Fact]
    public void DeferredUninstallCannotBeRolledBackIntoAnInstalledState()
    {
        var platform = new FakeInstallPlatform();
        var coordinator = CreateCoordinator(platform);
        coordinator.Install(CreateSource("version-one"), 45123, TrayInstallMode.Disabled);
        coordinator.Uninstall(UninstallReconnectPolicy.PreserveUntilNextSignIn);

        var exception = Assert.Throws<InvalidOperationException>(coordinator.Rollback);

        Assert.Contains("pending deferred uninstall", exception.Message);
    }

    [Fact]
    public void DeferredReconnectRestoreDoesNotClaimUrlChangedAfterInstall()
    {
        var platform = new FakeInstallPlatform();
        var coordinator = CreateCoordinator(platform);
        coordinator.Install(CreateSource("version-one"), 45123, TrayInstallMode.Disabled);
        platform.Environment[InstallCoordinator.AppServerUrlVariable] = "ws://127.0.0.1:49999";

        coordinator.Uninstall(UninstallReconnectPolicy.PreserveUntilNextSignIn);

        Assert.Equal(
            "ws://127.0.0.1:49999",
            platform.Environment[InstallCoordinator.AppServerUrlVariable]);
        Assert.InRange(
            platform.CleanupCommand!.Length,
            1,
            DeferredCleanupCommandBuilder.MaximumRunOnceCommandLength);
    }

    [Theory]
    [InlineData("continuity's live endpoint", "previous endpoint")]
    [InlineData("replacement endpoint", "replacement endpoint")]
    public async Task DeferredReconnectCleanupExecutesWithoutOverwritingANewerValue(
        string processValue,
        string expectedValue)
    {
        var cleanupRoot = Path.Combine(root, "deferred-cleanup");
        Directory.CreateDirectory(cleanupRoot);
        const string variableName = "CODEX_CONTINUITY_TEST_RECONNECT";
        const string appliedValue = "continuity's live endpoint";
        const string previousValue = "previous endpoint";
        var command = DeferredCleanupCommandBuilder.Build(
            [cleanupRoot],
            [
                new DeferredEnvironmentRestore(
                    variableName,
                    new OwnedString(previousValue, appliedValue)),
            ]);
        Assert.InRange(command.Length, 1, DeferredCleanupCommandBuilder.MaximumRunOnceCommandLength);
        var scriptPath = CleanupScriptPath(command);
        var script = File.ReadAllText(scriptPath).Replace(
            "[System.EnvironmentVariableTarget]::User",
            "[System.EnvironmentVariableTarget]::Process",
            StringComparison.Ordinal);
        script +=
            $"; Write-Output ([Environment]::GetEnvironmentVariable('{variableName}', " +
            "[System.EnvironmentVariableTarget]::Process))";
        File.WriteAllText(
            scriptPath,
            script,
            new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            Arguments = $"/d /s /c \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment[variableName] = processValue;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var standardOutput = await output;
        var standardError = await error;
        Assert.True(
            process.ExitCode == 0,
            $"Cleanup command exited with {process.ExitCode}: {standardError}");
        Assert.Equal(expectedValue, standardOutput.Trim());
        Assert.False(Directory.Exists(cleanupRoot));
        Assert.False(File.Exists(scriptPath));
    }

    [Fact]
    public void UninstallPreservesEveryValueChangedAfterInstall()
    {
        var platform = new FakeInstallPlatform();
        var coordinator = CreateCoordinator(platform);
        coordinator.Install(CreateBundleSource("version-one"), 45123, TrayInstallMode.Enabled);
        platform.Environment[InstallCoordinator.AppServerUrlVariable] = "ws://127.0.0.1:49999";
        platform.Environment[InstallCoordinator.DisableUpdaterVariable] = "true";
        platform.Environment[InstallCoordinator.PathVariable] = @"C:\ReplacementTools";
        platform.StartupCommand = "replacement supervisor startup";
        platform.TrayStartupCommand = "replacement tray startup";
        var replacementRegistration = platform.InstalledAppRegistration! with
        {
            DisplayName = "Replacement registration",
        };
        platform.InstalledAppRegistration = replacementRegistration;

        coordinator.Uninstall();

        Assert.Equal(
            "ws://127.0.0.1:49999",
            platform.Environment[InstallCoordinator.AppServerUrlVariable]);
        Assert.Equal(
            "true",
            platform.Environment[InstallCoordinator.DisableUpdaterVariable]);
        Assert.Equal(
            @"C:\ReplacementTools",
            platform.Environment[InstallCoordinator.PathVariable]);
        Assert.Equal("replacement supervisor startup", platform.StartupCommand);
        Assert.Equal("replacement tray startup", platform.TrayStartupCommand);
        Assert.Equal(replacementRegistration, platform.InstalledAppRegistration);
    }

    [Fact]
    public void ForeignReadyEndpointFailsBeforeAnyInstallMutation()
    {
        var platform = new FakeInstallPlatform
        {
            StartupCommand = "user startup",
        };
        platform.Environment[InstallCoordinator.AppServerUrlVariable] = "ws://127.0.0.1:49999";
        platform.Environment[InstallCoordinator.DisableUpdaterVariable] = "true";
        var coordinator = CreateCoordinator(platform);

        Assert.Throws<InvalidOperationException>(() => coordinator.Install(
            CreateSource("version-one"),
            45123,
            TrayInstallMode.Disabled,
            ExistingEndpointOwnership.Foreign));

        Assert.Equal("ws://127.0.0.1:49999", platform.Environment[InstallCoordinator.AppServerUrlVariable]);
        Assert.Equal("true", platform.Environment[InstallCoordinator.DisableUpdaterVariable]);
        Assert.Equal("user startup", platform.StartupCommand);
        Assert.Null(platform.InstalledAppRegistration);
        Assert.Null(platform.CleanupCommand);
        Assert.False(File.Exists(ContinuityPaths.InstallStateFile(root)));
    }

    [Fact]
    public void UpgradeFromLegacyInstallDoesNotRestoreLegacyConfigurationOnUninstall()
    {
        const int legacyPort = 45124;
        Directory.CreateDirectory(root);
        var legacyExecutable = Path.Combine(root, "CodexContinuity.exe");
        File.WriteAllText(legacyExecutable, "legacy-version");
        var platform = new FakeInstallPlatform
        {
            StartupCommand = StartupCommandBuilder.Build(legacyExecutable, legacyPort),
        };
        platform.Environment[InstallCoordinator.AppServerUrlVariable] =
            LoopbackEndpoint.WebSocketUrl(legacyPort);
        platform.Environment[InstallCoordinator.DisableUpdaterVariable] = "false";
        var coordinator = CreateCoordinator(platform);

        coordinator.Install(CreateSource("version-two"), 45123, TrayInstallMode.Disabled);
        coordinator.Uninstall();

        Assert.Null(platform.Environment[InstallCoordinator.AppServerUrlVariable]);
        Assert.Null(platform.Environment[InstallCoordinator.DisableUpdaterVariable]);
        Assert.Null(platform.StartupCommand);
        Assert.Null(platform.InstalledAppRegistration);
    }

    [Fact]
    public void LegacyMigrationRecognizesPowerShellEscapedApostropheInInstallPath()
    {
        const int legacyPort = 45124;
        var quotedRoot = Path.Combine(root, "owner's-data");
        Directory.CreateDirectory(quotedRoot);
        var legacyExecutable = Path.Combine(quotedRoot, "CodexContinuity.exe");
        File.WriteAllText(legacyExecutable, "legacy-version");
        var platform = new FakeInstallPlatform
        {
            StartupCommand = StartupCommandBuilder.Build(legacyExecutable, legacyPort),
        };
        platform.Environment[InstallCoordinator.AppServerUrlVariable] =
            LoopbackEndpoint.WebSocketUrl(legacyPort);
        platform.Environment[InstallCoordinator.DisableUpdaterVariable] = "false";
        var coordinator = new InstallCoordinator(
            quotedRoot,
            platform,
            new InstallStateStore(ContinuityPaths.InstallStateFile(quotedRoot)));

        coordinator.Install(CreateSource("version-two"), 45123, TrayInstallMode.Disabled);
        coordinator.Uninstall();

        Assert.Null(platform.Environment[InstallCoordinator.AppServerUrlVariable]);
        Assert.Null(platform.Environment[InstallCoordinator.DisableUpdaterVariable]);
        Assert.Null(platform.StartupCommand);
        Assert.Contains(
            quotedRoot.Replace("'", "''", StringComparison.Ordinal),
            ReadCleanupScript(platform.CleanupCommand));
    }

    [Fact]
    public void LegacyUninstallPreservesAppServerUrlChangedAfterInstall()
    {
        const int legacyPort = 45124;
        Directory.CreateDirectory(root);
        var legacyExecutable = Path.Combine(root, "CodexContinuity.exe");
        File.WriteAllText(legacyExecutable, "legacy-version");
        var platform = new FakeInstallPlatform
        {
            StartupCommand = StartupCommandBuilder.Build(legacyExecutable, legacyPort),
        };
        platform.Environment[InstallCoordinator.AppServerUrlVariable] =
            LoopbackEndpoint.WebSocketUrl(45125);
        platform.Environment[InstallCoordinator.DisableUpdaterVariable] = "false";
        var coordinator = CreateCoordinator(platform);

        coordinator.Uninstall();

        Assert.Equal(
            LoopbackEndpoint.WebSocketUrl(45125),
            platform.Environment[InstallCoordinator.AppServerUrlVariable]);
        Assert.Null(platform.StartupCommand);
    }

    [Fact]
    public void LegacyUninstallKeepsLiveReconnectUrlUntilNextSignIn()
    {
        const int legacyPort = 45124;
        Directory.CreateDirectory(root);
        var legacyExecutable = Path.Combine(root, "CodexContinuity.exe");
        File.WriteAllText(legacyExecutable, "legacy-version");
        var platform = new FakeInstallPlatform
        {
            StartupCommand = StartupCommandBuilder.Build(legacyExecutable, legacyPort),
        };
        platform.Environment[InstallCoordinator.AppServerUrlVariable] =
            LoopbackEndpoint.WebSocketUrl(legacyPort);
        var coordinator = CreateCoordinator(platform);

        coordinator.Uninstall(UninstallReconnectPolicy.PreserveUntilNextSignIn);

        Assert.Equal(
            LoopbackEndpoint.WebSocketUrl(legacyPort),
            platform.Environment[InstallCoordinator.AppServerUrlVariable]);
        Assert.Null(platform.StartupCommand);
        var cleanupScript = ReadCleanupScript(platform.CleanupCommand);
        Assert.Contains(InstallCoordinator.AppServerUrlVariable, cleanupScript);
        Assert.Contains("$null", cleanupScript);
        Assert.Equal(
            InstallLifecycle.DeferredUninstall,
            new InstallStateStore(ContinuityPaths.InstallStateFile(root)).Load()!.Lifecycle);
    }

    [Theory]
    [InlineData(45124)]
    [InlineData(45125)]
    public void LegacyReinstallBeforeDeferredCleanupDoesNotClaimItsReconnectUrl(int reinstallPort)
    {
        const int legacyPort = 45124;
        Directory.CreateDirectory(root);
        var legacyExecutable = Path.Combine(root, "CodexContinuity.exe");
        File.WriteAllText(legacyExecutable, "legacy-version");
        var platform = new FakeInstallPlatform
        {
            StartupCommand = StartupCommandBuilder.Build(legacyExecutable, legacyPort),
        };
        platform.Environment[InstallCoordinator.AppServerUrlVariable] =
            LoopbackEndpoint.WebSocketUrl(legacyPort);
        platform.Environment[InstallCoordinator.DisableUpdaterVariable] = "false";
        var coordinator = CreateCoordinator(platform);
        coordinator.Uninstall(UninstallReconnectPolicy.PreserveUntilNextSignIn);

        var reinstalled = coordinator.Install(
            CreateSource("version-two"),
            reinstallPort,
            TrayInstallMode.Disabled);
        coordinator.Uninstall();

        Assert.Null(reinstalled.State.AppServerUrl.PreviousValue);
        Assert.Null(platform.Environment[InstallCoordinator.AppServerUrlVariable]);
        Assert.Null(platform.Environment[InstallCoordinator.DisableUpdaterVariable]);
    }

    [Fact]
    public void RollbackToLegacyBuildRemovesUnsupportedInstalledAppsCommands()
    {
        const int legacyPort = 45124;
        Directory.CreateDirectory(root);
        var legacyExecutable = Path.Combine(root, "CodexContinuity.exe");
        File.WriteAllText(legacyExecutable, "legacy-version");
        var platform = new FakeInstallPlatform
        {
            StartupCommand = StartupCommandBuilder.Build(legacyExecutable, legacyPort),
        };
        platform.Environment[InstallCoordinator.AppServerUrlVariable] =
            LoopbackEndpoint.WebSocketUrl(legacyPort);
        platform.Environment[InstallCoordinator.DisableUpdaterVariable] = "false";
        var coordinator = CreateCoordinator(platform);
        coordinator.Install(CreateSource("version-two"), 45123, TrayInstallMode.Disabled);

        var rolledBack = coordinator.Rollback();

        Assert.Equal(legacyExecutable, rolledBack.InstalledExecutable);
        Assert.Null(rolledBack.InstalledAppRegistration);
        Assert.Null(platform.InstalledAppRegistration);
        Assert.Contains(legacyExecutable, platform.StartupCommand);
    }

    [Fact]
    public void UpgradeStagesNewVersionAndRollbackOnlyChangesFutureStartup()
    {
        var platform = new FakeInstallPlatform();
        var coordinator = CreateCoordinator(platform);
        var first = coordinator.Install(
            CreateSource("version-one"),
            45123,
            TrayInstallMode.Disabled);
        var second = coordinator.Install(
            CreateSource("version-two"),
            45123,
            TrayInstallMode.Disabled);

        var rolledBack = coordinator.Rollback();

        Assert.True(second.StagedUpgrade);
        Assert.True(second.CurrentBackendUnchanged);
        Assert.NotEqual(first.State.InstalledExecutable, second.State.InstalledExecutable);
        Assert.True(File.Exists(first.State.InstalledExecutable));
        Assert.True(File.Exists(second.State.InstalledExecutable));
        Assert.Equal("version-two", File.ReadAllText(ContinuityPaths.CommandExecutable(root)));
        Assert.Equal(first.State.InstalledExecutable, rolledBack.InstalledExecutable);
        Assert.Contains(first.State.InstalledExecutable, platform.StartupCommand);
        Assert.Equal(root, platform.InstalledAppRegistration?.InstallLocation);
        Assert.Contains(
            ContinuityPaths.CommandExecutable(root),
            platform.InstalledAppRegistration?.UninstallCommand);
    }

    [Fact]
    public void DefaultBundleStagesDisposableTrayWithIndependentStartup()
    {
        var platform = new FakeInstallPlatform();
        var coordinator = CreateCoordinator(platform);
        var source = CreateBundleSource("version-with-tray");

        var outcome = coordinator.Install(source, 45123, TrayInstallMode.Enabled);
        var supervisorStartup = platform.StartupCommand;
        Assert.Equal(
            "https://continuity.alirezaafshan.com",
            platform.InstalledAppRegistration?.UrlInfoAbout);
        coordinator.Uninstall();

        Assert.True(File.Exists(outcome.State.InstalledTrayExecutable));
        Assert.True(File.Exists(Path.Combine(
            ContinuityPaths.CommandDirectory(root),
            "CodexContinuity.Tray.exe")));
        Assert.Contains("CodexContinuity.Tray.exe", platform.AppliedTrayStartupCommand);
        Assert.DoesNotContain("CodexContinuity.Tray", supervisorStartup);
        Assert.Null(platform.TrayStartupCommand);
    }

    [Fact]
    public void DisablingTrayThenRollingBackRestoresPreviousTrayBuild()
    {
        var platform = new FakeInstallPlatform();
        var coordinator = CreateCoordinator(platform);
        var first = coordinator.Install(
            CreateBundleSource("version-one"),
            45123,
            TrayInstallMode.Enabled);

        var second = coordinator.Install(
            CreateSource("version-two"),
            45123,
            TrayInstallMode.Disabled);
        var rolledBack = coordinator.Rollback();

        Assert.Null(second.State.InstalledTrayExecutable);
        Assert.Null(second.State.TrayStartupCommand);
        Assert.Equal(first.State.InstalledTrayExecutable, rolledBack.InstalledTrayExecutable);
        Assert.Contains(first.State.InstalledTrayExecutable!, platform.TrayStartupCommand);
    }

    [Fact]
    public void InstallPublishesStableCommandOnAnOwnedPathAndCancelsPendingCleanup()
    {
        var platform = new FakeInstallPlatform
        {
            CleanupCommand = "previous deferred cleanup",
        };
        platform.Environment[InstallCoordinator.PathVariable] = @"C:\UserTools";
        var coordinator = CreateCoordinator(platform);

        var outcome = coordinator.Install(
            CreateSource("version-one"),
            45123,
            TrayInstallMode.Disabled);

        var commandExecutable = ContinuityPaths.CommandExecutable(root);
        Assert.Equal(4, outcome.State.SchemaVersion);
        Assert.Equal("version-one", File.ReadAllText(commandExecutable));
        Assert.Equal(
            $@"C:\UserTools;{ContinuityPaths.CommandDirectory(root)}",
            platform.Environment[InstallCoordinator.PathVariable]);
        Assert.Equal(
            new OwnedString(
                @"C:\UserTools",
                $@"C:\UserTools;{ContinuityPaths.CommandDirectory(root)}"),
            outcome.State.CommandPath);
        Assert.StartsWith($"\"{commandExecutable}\"", platform.InstalledAppRegistration!.ModifyCommand);
        Assert.Null(platform.CleanupCommand);
    }

    [Fact]
    public void PreexistingCommandPathIsNotClaimedOrRemoved()
    {
        var platform = new FakeInstallPlatform();
        var originalPath = $"C:\\UserTools;\"{ContinuityPaths.CommandDirectory(root)}\"";
        platform.Environment[InstallCoordinator.PathVariable] = originalPath;
        var coordinator = CreateCoordinator(platform);

        var outcome = coordinator.Install(
            CreateSource("version-one"),
            45123,
            TrayInstallMode.Disabled);
        coordinator.Uninstall();

        Assert.Null(outcome.State.CommandPath);
        Assert.Equal(originalPath, platform.Environment[InstallCoordinator.PathVariable]);
    }

    [Fact]
    public void DeferredCleanupRefusesFilesystemRoot()
    {
        var filesystemRoot = Path.GetPathRoot(root)!;

        Assert.Throws<InvalidOperationException>(() =>
            DeferredCleanupCommandBuilder.Build(filesystemRoot));
    }

    [Fact]
    public void MigrationLeavesOpenAiInstallOnlyUntilTheNextSignIn()
    {
        var legacyRoot = Path.Combine(root, "OpenAI", "CodexContinuity");
        var currentRoot = Path.Combine(root, "YesterdaysLemon", "CodexContinuity");
        var platform = new FakeInstallPlatform();
        var legacyCoordinator = new InstallCoordinator(
            legacyRoot,
            platform,
            new InstallStateStore(ContinuityPaths.InstallStateFile(legacyRoot)));
        var legacy = legacyCoordinator.Install(
            CreateBundleSource("version-one"),
            45123,
            TrayInstallMode.Enabled);
        var coordinator = new InstallCoordinator(
            currentRoot,
            platform,
            new InstallStateStore(ContinuityPaths.InstallStateFile(currentRoot)),
            legacyRoot);

        var migrated = coordinator.Install(
            CreateBundleSource("version-two"),
            45123,
            TrayInstallMode.Enabled);

        Assert.Equal(legacy.State.InstalledExecutable, migrated.State.PreviousInstalledExecutable);
        Assert.Equal(
            ContinuityPaths.CommandDirectory(currentRoot),
            platform.Environment[InstallCoordinator.PathVariable]);
        var migrationCleanupScript = ReadCleanupScript(platform.CleanupCommand);
        Assert.Contains(Path.GetFullPath(legacyRoot), migrationCleanupScript);
        Assert.DoesNotContain(Path.GetFullPath(currentRoot), migrationCleanupScript);
        Assert.True(File.Exists(ContinuityPaths.InstallStateFile(legacyRoot)));
        Assert.True(File.Exists(ContinuityPaths.InstallStateFile(currentRoot)));

        coordinator.Uninstall();

        Assert.Null(platform.Environment[InstallCoordinator.PathVariable]);
        var uninstallCleanupScript = ReadCleanupScript(platform.CleanupCommand);
        Assert.Contains(Path.GetFullPath(legacyRoot), uninstallCleanupScript);
        Assert.Contains(Path.GetFullPath(currentRoot), uninstallCleanupScript);
        Assert.False(File.Exists(ContinuityPaths.InstallStateFile(legacyRoot)));
        Assert.False(File.Exists(ContinuityPaths.InstallStateFile(currentRoot)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void FailedInstallRestoresEveryPlatformValue(int failingMutation)
    {
        var previousRegistration = new InstalledAppRegistration(
            "Previous product",
            "1.0.0",
            "Previous publisher",
            root,
            "previous.ico",
            "previous uninstall",
            "previous quiet uninstall",
            "previous repair",
            "https://example.test",
            42);
        var platform = new FakeInstallPlatform
        {
            StartupCommand = "previous startup",
            TrayStartupCommand = "previous tray startup",
            InstalledAppRegistration = previousRegistration,
            CleanupCommand = "previous deferred cleanup",
        };
        platform.Environment[InstallCoordinator.AppServerUrlVariable] = "ws://127.0.0.1:40000";
        platform.Environment[InstallCoordinator.DisableUpdaterVariable] = "true";
        platform.Environment[InstallCoordinator.PathVariable] = @"C:\PreviousTools";
        var coordinator = CreateCoordinator(platform);
        platform.FailAfterNextMutations(failingMutation);

        Assert.Throws<InvalidOperationException>(() => coordinator.Install(
            CreateBundleSource("version-one"),
            45123,
            TrayInstallMode.Enabled));

        Assert.Equal("ws://127.0.0.1:40000", platform.Environment[InstallCoordinator.AppServerUrlVariable]);
        Assert.Equal("true", platform.Environment[InstallCoordinator.DisableUpdaterVariable]);
        Assert.Equal(@"C:\PreviousTools", platform.Environment[InstallCoordinator.PathVariable]);
        Assert.Equal("previous startup", platform.StartupCommand);
        Assert.Equal("previous tray startup", platform.TrayStartupCommand);
        Assert.Equal(previousRegistration, platform.InstalledAppRegistration);
        Assert.Equal("previous deferred cleanup", platform.CleanupCommand);
        Assert.False(File.Exists(ContinuityPaths.InstallStateFile(root)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FailedRollbackRestoresPlatformAndPersistedState(int failingMutation)
    {
        var platform = new FakeInstallPlatform();
        var coordinator = CreateCoordinator(platform);
        coordinator.Install(CreateBundleSource("version-one"), 45123, TrayInstallMode.Enabled);
        coordinator.Install(CreateBundleSource("version-two"), 45123, TrayInstallMode.Enabled);
        var statePath = ContinuityPaths.InstallStateFile(root);
        var stateBefore = File.ReadAllText(statePath);
        var startupBefore = platform.StartupCommand;
        var trayStartupBefore = platform.TrayStartupCommand;
        var registrationBefore = platform.InstalledAppRegistration;
        platform.FailAfterNextMutations(failingMutation);

        Assert.Throws<InvalidOperationException>(coordinator.Rollback);

        Assert.Equal(startupBefore, platform.StartupCommand);
        Assert.Equal(trayStartupBefore, platform.TrayStartupCommand);
        Assert.Equal(registrationBefore, platform.InstalledAppRegistration);
        Assert.Equal(stateBefore, File.ReadAllText(statePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private InstallCoordinator CreateCoordinator(FakeInstallPlatform platform)
    {
        Directory.CreateDirectory(root);
        return new InstallCoordinator(
            root,
            platform,
            new InstallStateStore(ContinuityPaths.InstallStateFile(root)));
    }

    private string CreateSource(string content)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"source-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, content);
        return path;
    }

    private string CreateBundleSource(string content)
    {
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var supervisor = Path.Combine(directory, "CodexContinuity.exe");
        File.WriteAllText(supervisor, content);
        File.WriteAllText(Path.Combine(directory, "CodexContinuity.Tray.exe"), $"{content}-tray");
        return supervisor;
    }

    private static string ReadCleanupScript(string? cleanupCommand) =>
        File.ReadAllText(CleanupScriptPath(cleanupCommand));

    private static string CleanupScriptPath(string? cleanupCommand)
    {
        Assert.NotNull(cleanupCommand);
        const string marker = "-File \"";
        var markerIndex = cleanupCommand.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"Expected a file-backed cleanup command: {cleanupCommand}");
        var pathStart = markerIndex + marker.Length;
        var pathEnd = cleanupCommand.IndexOf('"', pathStart);
        Assert.True(pathEnd > pathStart, $"Expected a quoted cleanup script path: {cleanupCommand}");
        return cleanupCommand[pathStart..pathEnd];
    }

    private sealed class FakeInstallPlatform : IInstallPlatform
    {
        private int mutationCount;
        private int? failingMutation;

        internal Dictionary<string, string?> Environment { get; } = [];
        internal string? StartupCommand { get; set; }
        internal string? TrayStartupCommand { get; set; }
        internal string? AppliedTrayStartupCommand { get; private set; }
        internal InstalledAppRegistration? InstalledAppRegistration { get; set; }
        internal string? CleanupCommand { get; set; }

        internal void FailAfterNextMutations(int mutationOffset) =>
            failingMutation = mutationCount + mutationOffset;

        public string? GetUserEnvironmentVariable(string name) =>
            Environment.GetValueOrDefault(name);

        public void SetUserEnvironmentVariable(string name, string? value)
        {
            ThrowIfRequested();
            Environment[name] = value;
        }

        public string? GetStartupCommand() => StartupCommand;

        public void SetStartupCommand(string? value)
        {
            ThrowIfRequested();
            StartupCommand = value;
        }

        public string? GetTrayStartupCommand() => TrayStartupCommand;

        public void SetTrayStartupCommand(string? value)
        {
            ThrowIfRequested();
            TrayStartupCommand = value;
            if (value is not null)
            {
                AppliedTrayStartupCommand = value;
            }
        }

        public InstalledAppRegistration? GetInstalledAppRegistration() =>
            InstalledAppRegistration;

        public void SetInstalledAppRegistration(InstalledAppRegistration? registration)
        {
            ThrowIfRequested();
            InstalledAppRegistration = registration;
        }

        public string? GetCleanupCommand() => CleanupCommand;

        public void SetCleanupCommand(string? value)
        {
            ThrowIfRequested();
            CleanupCommand = value;
        }

        public void BroadcastEnvironmentChange()
        {
        }

        private void ThrowIfRequested()
        {
            mutationCount++;
            if (mutationCount == failingMutation)
            {
                throw new InvalidOperationException("Injected platform mutation failure.");
            }
        }
    }
}
