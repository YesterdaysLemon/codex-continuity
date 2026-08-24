using Xunit;

namespace CodexContinuity.Tests;

public sealed class InstallFileStagerTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-file-stager-tests-{Guid.NewGuid():N}");

    public InstallFileStagerTests() => Directory.CreateDirectory(root);

    [Fact]
    public void StageVersionCopiesSupervisorAndTrayWithVerifiedHashes()
    {
        var supervisor = WriteSource("CodexContinuity.exe", "supervisor-v1");
        var tray = WriteSource("CodexContinuity.Tray.exe", "tray-v1");
        var stager = new InstallFileStager(root);

        var staged = stager.StageVersion(
            supervisor,
            tray,
            InstallFileStager.ComputeSha256(supervisor));

        Assert.Equal("supervisor-v1", File.ReadAllText(staged.SupervisorExecutable));
        Assert.NotNull(staged.TrayExecutable);
        Assert.Equal("tray-v1", File.ReadAllText(staged.TrayExecutable!));
        Assert.Equal(
            InstallFileStager.ComputeSha256(supervisor),
            InstallFileStager.ComputeSha256(staged.SupervisorExecutable));
        Assert.Equal(
            InstallFileStager.ComputeSha256(tray),
            InstallFileStager.ComputeSha256(staged.TrayExecutable!));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void StageVersionRejectsATamperedExistingVersionWithoutReplacingIt()
    {
        var supervisor = WriteSource("CodexContinuity.exe", "supervisor-v1");
        var stager = new InstallFileStager(root);
        var hash = InstallFileStager.ComputeSha256(supervisor);
        var staged = stager.StageVersion(supervisor, null, hash);
        File.WriteAllText(staged.SupervisorExecutable, "tampered");

        Assert.Throws<InvalidDataException>(() => stager.StageVersion(supervisor, null, hash));
        Assert.Equal("tampered", File.ReadAllText(staged.SupervisorExecutable));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void PublishCommandExecutablePreservesDestinationWhenVerificationFails()
    {
        var supervisor = WriteSource("CodexContinuity.exe", "supervisor-v1");
        var tray = WriteSource("CodexContinuity.Tray.exe", "tray-v1");
        var stager = new InstallFileStager(root);
        var hash = InstallFileStager.ComputeSha256(supervisor);
        var destination = stager.PublishCommandExecutable(supervisor, tray, hash);
        var previousSupervisor = File.ReadAllText(destination);
        var previousTray = File.ReadAllText(
            Path.Combine(ContinuityPaths.CommandDirectory(root), "CodexContinuity.Tray.exe"));
        File.WriteAllText(supervisor, "supervisor-v2");

        Assert.Throws<InvalidDataException>(() => stager.PublishCommandExecutable(
            supervisor,
            tray,
            new string('0', 64)));

        Assert.Equal(previousSupervisor, File.ReadAllText(destination));
        Assert.Equal(
            previousTray,
            File.ReadAllText(
                Path.Combine(ContinuityPaths.CommandDirectory(root), "CodexContinuity.Tray.exe")));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void CoordinatorUsesInjectedStagerBeforePlatformMutation()
    {
        var source = WriteSource("CodexContinuity.exe", "supervisor-v1");
        var events = new List<string>();
        var platform = new RecordingInstallPlatform(events);
        var stager = new RecordingInstallFileStager(root, events);
        var coordinator = new InstallCoordinator(
            root,
            platform,
            new InstallStateStore(ContinuityPaths.InstallStateFile(root)),
            fileStager: stager);

        var outcome = coordinator.Install(source, 45123, TrayInstallMode.Disabled);

        Assert.Equal(["stage", "publish"], events.Take(2));
        Assert.True(stager.SawLifecycleLock);
        Assert.Contains("platform:cleanup", events.Skip(2));
        Assert.Equal(stager.StagedExecutable, outcome.State.InstalledExecutable);
        Assert.Contains(
            stager.CommandExecutable,
            outcome.State.InstalledAppRegistration!.DisplayIcon);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private string WriteSource(string name, string content)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private void AssertNoTemporaryFiles() => Assert.Empty(
        Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));

    private sealed class RecordingInstallFileStager(
        string root,
        List<string> events) : IInstallFileStager
    {
        internal string StagedExecutable { get; private set; } = string.Empty;
        internal string CommandExecutable { get; private set; } = string.Empty;
        internal bool SawLifecycleLock { get; private set; }

        public StagedInstallVersion StageVersion(
            string sourceExecutable,
            string? sourceTrayExecutable,
            string hash)
        {
            events.Add("stage");
            SawLifecycleLock = File.Exists(ContinuityPaths.LifecycleLockFile(root));
            var directory = Path.Combine(root, "injected-stager");
            Directory.CreateDirectory(directory);
            StagedExecutable = Path.Combine(directory, "staged.exe");
            File.Copy(sourceExecutable, StagedExecutable, overwrite: true);
            return new(StagedExecutable, TrayExecutable: null);
        }

        public string PublishCommandExecutable(
            string sourceExecutable,
            string? sourceTrayExecutable,
            string hash)
        {
            events.Add("publish");
            var directory = Path.Combine(root, "injected-stager");
            Directory.CreateDirectory(directory);
            CommandExecutable = Path.Combine(directory, "command.exe");
            File.Copy(sourceExecutable, CommandExecutable, overwrite: true);
            return CommandExecutable;
        }
    }

    private sealed class RecordingInstallPlatform(List<string> events) : IInstallPlatform
    {
        public string? GetUserEnvironmentVariable(string name) => null;

        public void SetUserEnvironmentVariable(string name, string? value) =>
            events.Add($"platform:environment:{name}");

        public string? GetStartupCommand() => null;

        public void SetStartupCommand(string? value) => events.Add("platform:startup");

        public string? GetTrayStartupCommand() => null;

        public void SetTrayStartupCommand(string? value) => events.Add("platform:tray");

        public InstalledAppRegistration? GetInstalledAppRegistration() => null;

        public void SetInstalledAppRegistration(InstalledAppRegistration? registration) =>
            events.Add("platform:registration");

        public string? GetCleanupCommand() => null;

        public void SetCleanupCommand(string? value) => events.Add("platform:cleanup");

        public void BroadcastEnvironmentChange() => events.Add("platform:broadcast");
    }
}
