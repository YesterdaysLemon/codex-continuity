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
            InstallFileStager.ComputeSha256(supervisor),
            InstallFileStager.ComputeSha256(tray));

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
        var staged = stager.StageVersion(supervisor, null, hash, null);
        File.WriteAllText(staged.SupervisorExecutable, "tampered");

        Assert.Throws<InvalidDataException>(() => stager.StageVersion(supervisor, null, hash, null));
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
        var trayHash = InstallFileStager.ComputeSha256(tray);
        var destination = stager.PublishCommandExecutable(supervisor, tray, hash, trayHash);
        var previousSupervisor = File.ReadAllText(destination);
        var previousTray = File.ReadAllText(
            Path.Combine(ContinuityPaths.CommandDirectory(root), "CodexContinuity.Tray.exe"));
        File.WriteAllText(supervisor, "supervisor-v2");

        Assert.Throws<InvalidDataException>(() => stager.PublishCommandExecutable(
            supervisor,
            tray,
            new string('0', 64),
            trayHash));

        Assert.Equal(previousSupervisor, File.ReadAllText(destination));
        Assert.Equal(
            previousTray,
            File.ReadAllText(
                Path.Combine(ContinuityPaths.CommandDirectory(root), "CodexContinuity.Tray.exe")));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void StagerRejectsInvalidExpectedDigestsAndTrayCombinations()
    {
        var supervisor = WriteSource("CodexContinuity.exe", "supervisor-v1");
        var tray = WriteSource("CodexContinuity.Tray.exe", "tray-v1");
        var supervisorHash = InstallFileStager.ComputeSha256(supervisor);
        var trayHash = InstallFileStager.ComputeSha256(tray);
        var stager = new InstallFileStager(root);

        Assert.Throws<ArgumentException>(() => stager.StageVersion(
            supervisor,
            null,
            new string('0', 63),
            null));
        Assert.Throws<ArgumentException>(() => stager.StageVersion(
            supervisor,
            tray,
            supervisorHash,
            null));
        Assert.Throws<ArgumentException>(() => stager.PublishCommandExecutable(
            supervisor,
            null,
            supervisorHash,
            trayHash));
    }

    [Fact]
    public void StageVersionRejectsTraySourceChangedAfterExpectedDigestWasComputed()
    {
        var supervisor = WriteSource("CodexContinuity.exe", "supervisor-v1");
        var tray = WriteSource("CodexContinuity.Tray.exe", "tray-v1");
        var supervisorHash = InstallFileStager.ComputeSha256(supervisor);
        var trayHash = InstallFileStager.ComputeSha256(tray);
        File.WriteAllText(tray, "tray-mutated-after-hash");

        Assert.Throws<InvalidDataException>(() => new InstallFileStager(root).StageVersion(
            supervisor,
            tray,
            supervisorHash,
            trayHash));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void CoordinatorRejectsSourceChangedAfterExpectedHashWasComputed()
    {
        var source = WriteSource("CodexContinuity.exe", "supervisor-v1");
        var events = new List<string>();
        var platform = new RecordingInstallPlatform(events);
        var stager = new SourceMutatingInstallFileStager(root);
        var coordinator = new InstallCoordinator(
            root,
            platform,
            new InstallStateStore(ContinuityPaths.InstallStateFile(root)),
            fileStager: stager);

        Assert.Throws<InvalidDataException>(() => coordinator.Install(
            source,
            45123,
            TrayInstallMode.Disabled));

        Assert.DoesNotContain(events, entry => entry.StartsWith("platform:", StringComparison.Ordinal));
        Assert.False(File.Exists(ContinuityPaths.InstallStateFile(root)));
    }

    [Fact]
    public void CoordinatorRejectsInjectedExternalVersionedPathBeforePlatformMutation()
    {
        var source = WriteSource("CodexContinuity.exe", "supervisor-v1");
        var external = WriteSource("external.exe", "supervisor-v1");

        AssertInjectedStagerRejected(
            source,
            TrayInstallMode.Disabled,
            new StagedInstallVersion(external, TrayExecutable: null));
    }

    [Fact]
    public void CoordinatorRejectsInjectedMissingVersionedPathBeforePlatformMutation()
    {
        var source = WriteSource("CodexContinuity.exe", "supervisor-v1");
        var missing = Path.Combine(
            ContinuityPaths.VersionsDirectory(root),
            "attacker",
            "CodexContinuity.exe");

        AssertInjectedStagerRejected(
            source,
            TrayInstallMode.Disabled,
            new StagedInstallVersion(missing, TrayExecutable: null));
    }

    [Fact]
    public void CoordinatorRejectsInjectedMismatchedVersionedPathBeforePlatformMutation()
    {
        var source = WriteSource("CodexContinuity.exe", "supervisor-v1");
        var mismatched = WriteSource(
            Path.Combine("versions", "attacker", "CodexContinuity.exe"),
            "not-the-supervisor");

        AssertInjectedStagerRejected(
            source,
            TrayInstallMode.Disabled,
            new StagedInstallVersion(mismatched, TrayExecutable: null));
    }

    [Fact]
    public void CoordinatorRejectsInjectedCommandPathOutsideTheBinDirectory()
    {
        var source = WriteSource("CodexContinuity.exe", "supervisor-v1");
        var staged = WriteSource(
            Path.Combine("versions", "attacker", "CodexContinuity.exe"),
            "supervisor-v1");
        var externalCommand = WriteSource("external-command.exe", "supervisor-v1");

        AssertInjectedStagerRejected(
            source,
            TrayInstallMode.Disabled,
            new StagedInstallVersion(staged, TrayExecutable: null),
            externalCommand);
    }

    [Fact]
    public void CoordinatorRejectsUnexpectedTrayWhenTrayIsDisabled()
    {
        var source = WriteSource("CodexContinuity.exe", "supervisor-v1");
        var staged = WriteSource(
            Path.Combine("versions", "attacker", "CodexContinuity.exe"),
            "supervisor-v1");
        var unexpectedTray = WriteSource(
            Path.Combine("versions", "attacker", "CodexContinuity.Tray.exe"),
            "tray-v1");

        AssertInjectedStagerRejected(
            source,
            TrayInstallMode.Disabled,
            new StagedInstallVersion(staged, unexpectedTray));
    }

    [Fact]
    public void CoordinatorRejectsMissingTrayWhenTrayIsEnabled()
    {
        var source = WriteSource("CodexContinuity.exe", "supervisor-v1");
        WriteSource("CodexContinuity.Tray.exe", "tray-v1");
        var staged = WriteSource(
            Path.Combine("versions", "attacker", "CodexContinuity.exe"),
            "supervisor-v1");

        AssertInjectedStagerRejected(
            source,
            TrayInstallMode.Enabled,
            new StagedInstallVersion(staged, TrayExecutable: null));
    }

    [Fact]
    public void CoordinatorRejectsMissingPublishedTrayBeforePlatformMutation()
    {
        var source = WriteSource("CodexContinuity.exe", "supervisor-v1");
        WriteSource("CodexContinuity.Tray.exe", "tray-v1");
        var staged = WriteSource(
            Path.Combine("versions", "attacker", "CodexContinuity.exe"),
            "supervisor-v1");
        var stagedTray = WriteSource(
            Path.Combine("versions", "attacker", "CodexContinuity.Tray.exe"),
            "tray-v1");
        var command = WriteSource(
            Path.Combine("bin", "CodexContinuity.exe"),
            "supervisor-v1");

        AssertInjectedStagerRejected(
            source,
            TrayInstallMode.Enabled,
            new StagedInstallVersion(staged, stagedTray),
            command);
    }

    [Fact]
    public void CoordinatorRejectsInjectedAliasedSupervisorAndTrayBeforePlatformMutation()
    {
        var source = WriteSource("CodexContinuity.exe", "same-binary-content");
        var tray = WriteSource("CodexContinuity.Tray.exe", "same-binary-content");
        var aliased = WriteSource(
            Path.Combine("versions", "attacker", "CodexContinuity.exe"),
            "same-binary-content");

        AssertInjectedStagerRejected(
            source,
            TrayInstallMode.Enabled,
            new StagedInstallVersion(aliased, aliased));
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private void AssertNoTemporaryFiles() => Assert.Empty(
        Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));

    private void AssertInjectedStagerRejected(
        string source,
        TrayInstallMode trayInstallMode,
        StagedInstallVersion stagedVersion,
        string? commandExecutable = null)
    {
        var events = new List<string>();
        var platform = new RecordingInstallPlatform(events);
        var coordinator = new InstallCoordinator(
            root,
            platform,
            new InstallStateStore(ContinuityPaths.InstallStateFile(root)),
            fileStager: new FixedInstallFileStager(
                stagedVersion,
                commandExecutable ?? ContinuityPaths.CommandExecutable(root)));

        Assert.Throws<InvalidDataException>(() => coordinator.Install(
            source,
            45123,
            trayInstallMode));

        Assert.DoesNotContain(events, entry => entry.StartsWith("platform:", StringComparison.Ordinal));
        Assert.False(File.Exists(ContinuityPaths.InstallStateFile(root)));
    }

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
            string supervisorSha256,
            string? traySha256)
        {
            events.Add("stage");
            SawLifecycleLock = File.Exists(ContinuityPaths.LifecycleLockFile(root));
            var directory = Path.Combine(ContinuityPaths.VersionsDirectory(root), "injected");
            Directory.CreateDirectory(directory);
            StagedExecutable = Path.Combine(directory, "CodexContinuity.exe");
            File.Copy(sourceExecutable, StagedExecutable, overwrite: true);
            var trayExecutable = sourceTrayExecutable is null
                ? null
                : Path.Combine(directory, "CodexContinuity.Tray.exe");
            if (sourceTrayExecutable is not null)
            {
                File.Copy(sourceTrayExecutable, trayExecutable!, overwrite: true);
            }
            return new(StagedExecutable, trayExecutable);
        }

        public string PublishCommandExecutable(
            string sourceExecutable,
            string? sourceTrayExecutable,
            string supervisorSha256,
            string? traySha256)
        {
            events.Add("publish");
            CommandExecutable = ContinuityPaths.CommandExecutable(root);
            Directory.CreateDirectory(Path.GetDirectoryName(CommandExecutable)!);
            File.Copy(sourceExecutable, CommandExecutable, overwrite: true);
            return CommandExecutable;
        }
    }

    private sealed class SourceMutatingInstallFileStager(string root) : IInstallFileStager
    {
        public StagedInstallVersion StageVersion(
            string sourceExecutable,
            string? sourceTrayExecutable,
            string supervisorSha256,
            string? traySha256)
        {
            File.WriteAllText(sourceExecutable, "supervisor-mutated-after-hash");
            return new InstallFileStager(root).StageVersion(
                sourceExecutable,
                sourceTrayExecutable,
                supervisorSha256,
                traySha256);
        }

        public string PublishCommandExecutable(
            string sourceExecutable,
            string? sourceTrayExecutable,
            string supervisorSha256,
            string? traySha256) => throw new InvalidOperationException(
                "Publishing should not be reached after source hash verification fails.");
    }

    private sealed class FixedInstallFileStager(
        StagedInstallVersion stagedVersion,
        string commandExecutable) : IInstallFileStager
    {
        public StagedInstallVersion StageVersion(
            string sourceExecutable,
            string? sourceTrayExecutable,
            string supervisorSha256,
            string? traySha256) => stagedVersion;

        public string PublishCommandExecutable(
            string sourceExecutable,
            string? sourceTrayExecutable,
            string supervisorSha256,
            string? traySha256) => commandExecutable;
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
