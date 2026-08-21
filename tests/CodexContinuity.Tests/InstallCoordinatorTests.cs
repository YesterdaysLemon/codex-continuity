using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class InstallCoordinatorTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-install-tests-{Guid.NewGuid():N}");

    [Fact]
    public void CustomPortUninstallRestoresOwnedValuesWithoutRepeatingPort()
    {
        var platform = new FakeInstallPlatform
        {
            StartupCommand = "previous startup",
        };
        platform.Environment[InstallCoordinator.AppServerUrlVariable] = "ws://127.0.0.1:40000";
        platform.Environment[InstallCoordinator.DisableUpdaterVariable] = "true";
        var coordinator = CreateCoordinator(platform);
        var source = CreateSource("version-one");

        var outcome = coordinator.Install(source, 45124);
        var removed = coordinator.Uninstall();

        Assert.True(removed);
        Assert.Equal(45124, outcome.State.Port);
        Assert.Equal("ws://127.0.0.1:40000", platform.Environment[InstallCoordinator.AppServerUrlVariable]);
        Assert.Equal("true", platform.Environment[InstallCoordinator.DisableUpdaterVariable]);
        Assert.Equal("previous startup", platform.StartupCommand);
        Assert.Null(platform.InstalledAppRegistration);
    }

    [Fact]
    public void UninstallPreservesValuesChangedAfterInstall()
    {
        var platform = new FakeInstallPlatform();
        var coordinator = CreateCoordinator(platform);
        coordinator.Install(CreateSource("version-one"), 45123);
        platform.Environment[InstallCoordinator.AppServerUrlVariable] = "ws://127.0.0.1:49999";

        coordinator.Uninstall();

        Assert.Equal(
            "ws://127.0.0.1:49999",
            platform.Environment[InstallCoordinator.AppServerUrlVariable]);
    }

    [Fact]
    public void UpgradeStagesNewVersionAndRollbackOnlyChangesFutureStartup()
    {
        var platform = new FakeInstallPlatform();
        var coordinator = CreateCoordinator(platform);
        var first = coordinator.Install(CreateSource("version-one"), 45123);
        var second = coordinator.Install(CreateSource("version-two"), 45123);

        var rolledBack = coordinator.Rollback();

        Assert.True(second.StagedUpgrade);
        Assert.True(second.CurrentBackendUnchanged);
        Assert.NotEqual(first.State.InstalledExecutable, second.State.InstalledExecutable);
        Assert.True(File.Exists(first.State.InstalledExecutable));
        Assert.True(File.Exists(second.State.InstalledExecutable));
        Assert.Equal(first.State.InstalledExecutable, rolledBack.InstalledExecutable);
        Assert.Contains(first.State.InstalledExecutable, platform.StartupCommand);
        Assert.Equal(
            first.State.InstalledExecutable,
            platform.InstalledAppRegistration?.InstallLocation is { } installLocation
                ? Path.Combine(installLocation, "CodexContinuity.exe")
                : null);
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

    private sealed class FakeInstallPlatform : IInstallPlatform
    {
        internal Dictionary<string, string?> Environment { get; } = [];
        internal string? StartupCommand { get; set; }
        internal InstalledAppRegistration? InstalledAppRegistration { get; set; }

        public string? GetUserEnvironmentVariable(string name) =>
            Environment.GetValueOrDefault(name);

        public void SetUserEnvironmentVariable(string name, string? value) =>
            Environment[name] = value;

        public string? GetStartupCommand() => StartupCommand;

        public void SetStartupCommand(string? value) => StartupCommand = value;

        public InstalledAppRegistration? GetInstalledAppRegistration() =>
            InstalledAppRegistration;

        public void SetInstalledAppRegistration(InstalledAppRegistration? registration) =>
            InstalledAppRegistration = registration;

        public void BroadcastEnvironmentChange()
        {
        }
    }
}
