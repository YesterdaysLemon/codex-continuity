using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class StagedSupervisorTransitionTests
{
    [Fact]
    public void LoadsExactRunningSelectedAndRollbackBuildsFromIndependentState()
    {
        using var fixture = new TransitionFixture();

        Assert.Equal(
            new StagedSupervisorTransitionLoadResult(
                StagedSupervisorTransitionLoadKind.Loaded,
                new StagedSupervisorTransitionBuilds(
                    fixture.Rollback,
                    fixture.Selected,
                    fixture.Rollback)),
            fixture.Load());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("unsupportedSchema")]
    [InlineData("inactive")]
    [InlineData("missingRollback")]
    public void UnavailableInstallStateFailsClosed(string coordinate)
    {
        using var fixture = new TransitionFixture();
        var path = ContinuityPaths.InstallStateFile(fixture.Root);
        switch (coordinate)
        {
            case "missing":
                File.Delete(path);
                break;
            case "malformed":
                File.WriteAllText(path, "{");
                break;
            case "unsupportedSchema":
                fixture.InstallState = fixture.InstallState with { SchemaVersion = int.MaxValue };
                fixture.SaveInstallState();
                break;
            case "inactive":
                fixture.InstallState = fixture.InstallState with
                {
                    Lifecycle = InstallLifecycle.DeferredUninstall,
                };
                fixture.SaveInstallState();
                break;
            case "missingRollback":
                fixture.InstallState = fixture.InstallState with
                {
                    PreviousInstalledExecutable = null,
                };
                fixture.SaveInstallState();
                break;
            default:
                throw new InvalidOperationException($"Unknown install coordinate {coordinate}.");
        }

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.InstallStateUnavailable,
            fixture.Load().Kind);
    }

    [Fact]
    public void SelectedBuildMustBeIndependentlyVerified()
    {
        using var fixture = new TransitionFixture
        {
            SelectedLoad = new(
                ContinuitySelectedBuildLoadKind.UnverifiedExecutable,
                Build: null),
        };

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.SelectedBuildUnavailable,
            fixture.Load().Kind);
    }

    [Fact]
    public void SelectedBuildMustUseItsContentAddressedContinuityPath()
    {
        using var fixture = new TransitionFixture();
        fixture.InstallState = fixture.InstallState with
        {
            InstalledExecutable = Path.Combine(fixture.Root, "CodexContinuity.exe"),
        };
        fixture.SaveInstallState();

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.SelectedBuildUnavailable,
            fixture.Load().Kind);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    public void UnavailableUpdateStateFailsClosed(string coordinate)
    {
        using var fixture = new TransitionFixture();
        var path = ContinuityPaths.UpdateStatusFile(fixture.Root);
        if (coordinate == "missing")
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllText(path, "{");
        }

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.UpdateStateUnavailable,
            fixture.Load().Kind);
    }

    [Theory]
    [InlineData("runningUnobserved")]
    [InlineData("sameVersion")]
    [InlineData("missingStagedTime")]
    [InlineData("alreadyApplied")]
    [InlineData("selectedDigest")]
    [InlineData("installDigest")]
    [InlineData("duplicateRelease")]
    public void PendingSelectedReleaseMustMatchEveryIndependentCoordinate(string coordinate)
    {
        using var fixture = new TransitionFixture();
        var release = fixture.UpdateState.Releases.Single();
        switch (coordinate)
        {
            case "runningUnobserved":
                fixture.UpdateState = fixture.UpdateState with { RunningProcessObserved = false };
                break;
            case "sameVersion":
                fixture.UpdateState = fixture.UpdateState with
                {
                    SelectedVersion = fixture.UpdateState.RunningVersion,
                };
                break;
            case "missingStagedTime":
                fixture.UpdateState = fixture.UpdateState with
                {
                    Releases = [release with { StagedAtUtc = null }],
                };
                break;
            case "alreadyApplied":
                fixture.UpdateState = fixture.UpdateState with
                {
                    Releases = [release with { AppliedAtUtc = TransitionFixture.Now }],
                };
                break;
            case "selectedDigest":
                fixture.UpdateState = fixture.UpdateState with
                {
                    Releases = [release with { StagedExecutableSha256 = new string('c', 64) }],
                };
                break;
            case "installDigest":
                fixture.InstallState = fixture.InstallState with
                {
                    BinarySha256 = new string('c', 64),
                };
                fixture.SaveInstallState();
                break;
            case "duplicateRelease":
                fixture.UpdateState = fixture.UpdateState with { Releases = [release, release] };
                break;
            default:
                throw new InvalidOperationException($"Unknown update coordinate {coordinate}.");
        }
        fixture.SaveUpdateState();

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.NoPendingStagedUpdate,
            fixture.Load().Kind);
    }

    [Theory]
    [InlineData("missingExecutable")]
    [InlineData("version")]
    [InlineData("runningDigest")]
    [InlineData("rollbackDigest")]
    public void RollbackMustMatchTheRunningBuildAndStagedLedger(string coordinate)
    {
        using var fixture = new TransitionFixture();
        var release = fixture.UpdateState.Releases.Single();
        switch (coordinate)
        {
            case "missingExecutable":
                fixture.ResolvedRollback = null;
                break;
            case "version":
                fixture.ResolvedRollback = fixture.Rollback with { Version = "9.0.0" };
                break;
            case "runningDigest":
                fixture.UpdateState = fixture.UpdateState with
                {
                    RunningExecutableSha256 = new string('c', 64),
                };
                break;
            case "rollbackDigest":
                fixture.UpdateState = fixture.UpdateState with
                {
                    Releases = [release with { RollbackExecutableSha256 = new string('c', 64) }],
                };
                break;
            default:
                throw new InvalidOperationException($"Unknown rollback coordinate {coordinate}.");
        }
        fixture.SaveUpdateState();

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.RollbackBuildUnavailable,
            fixture.Load().Kind);
    }

    [Fact]
    public void RollbackBuildMustUseItsContentAddressedContinuityPath()
    {
        using var fixture = new TransitionFixture();
        fixture.ResolvedRollback = fixture.Rollback with
        {
            Executable = Path.Combine(fixture.Root, "CodexContinuity.exe"),
        };

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.RollbackBuildUnavailable,
            fixture.Load().Kind);
    }

    private sealed class TransitionFixture : IDisposable
    {
        internal static readonly DateTimeOffset Now =
            DateTimeOffset.Parse("2026-08-23T12:00:00Z");

        internal TransitionFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"codex-continuity-staged-transition-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Rollback = Build("0.3.0", 'a');
            Selected = Build("0.4.0", 'b');
            InstallState = new InstallState(
                InstallStateStore.CurrentSchemaVersion,
                Port: 45123,
                InstalledExecutable: Selected.Executable,
                PreviousInstalledExecutable: Rollback.Executable,
                InstalledTrayExecutable: null,
                PreviousInstalledTrayExecutable: null,
                BinarySha256: Selected.ExecutableSha256,
                AppServerUrl: new OwnedString(null, LoopbackEndpoint.WebSocketUrl(45123)),
                UpdaterSetting: new OwnedString(null, "false"),
                CommandPath: null,
                StartupCommand: new OwnedString(null, "fixture"),
                TrayStartupCommand: null,
                PreviousInstalledAppRegistration: null,
                InstalledAppRegistration: null,
                InstalledAtUtc: Now);
            var release = new TrackedContinuityRelease(
                Selected.Version,
                PublishedAtUtc: Now - TimeSpan.FromHours(1),
                FirstObservedAtUtc: Now - TimeSpan.FromMinutes(30),
                StagedAtUtc: Now,
                AppliedAtUtc: null,
                LastError: null,
                StagedExecutableSha256: Selected.ExecutableSha256,
                RollbackExecutableSha256: Rollback.ExecutableSha256);
            UpdateState = new ContinuityUpdateState(
                SchemaVersion: 1,
                TrackingStartedAtUtc: Now - TimeSpan.FromDays(1),
                LastCheckedAtUtc: Now,
                BaselineVersion: Rollback.Version,
                RunningVersion: Rollback.Version,
                SelectedVersion: Selected.Version,
                RunningProcessObserved: true,
                LatestVersion: Selected.Version,
                LastError: null,
                ObservedCount: 1,
                StagedCount: 1,
                AppliedCount: 0,
                Releases: [release],
                RunningExecutableSha256: Rollback.ExecutableSha256);
            SelectedLoad = new(
                ContinuitySelectedBuildLoadKind.Loaded,
                new ContinuityBuildIdentity(Selected.Version, Selected.ExecutableSha256));
            ResolvedRollback = Rollback;
            SaveInstallState();
            SaveUpdateState();
        }

        internal string Root { get; }
        internal SupervisorExecutableIdentity Selected { get; }
        internal SupervisorExecutableIdentity Rollback { get; }
        internal InstallState InstallState { get; set; }
        internal ContinuityUpdateState UpdateState { get; set; }
        internal ContinuitySelectedBuildLoadResult SelectedLoad { get; init; }
        internal SupervisorExecutableIdentity? ResolvedRollback { get; set; }

        internal StagedSupervisorTransitionLoadResult Load() =>
            StagedSupervisorTransitionReader.Load(
                Root,
                new StagedSupervisorTransitionChecks(
                    _ => SelectedLoad,
                    executable => PathsEqual(executable, Rollback.Executable)
                        ? ResolvedRollback
                        : null));

        internal void SaveInstallState() => new InstallStateStore(
            ContinuityPaths.InstallStateFile(Root)).Save(InstallState);

        internal void SaveUpdateState() => new ContinuityUpdateStateStore(
            ContinuityPaths.UpdateStatusFile(Root)).Save(UpdateState);

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private SupervisorExecutableIdentity Build(
            string version,
            char sha256Character)
        {
            var sha256 = new string(sha256Character, 64);
            return new(
                version,
                Path.Combine(
                    ContinuityPaths.VersionsDirectory(Root),
                    $"{version}-{sha256[..12]}",
                    "CodexContinuity.exe"),
                sha256);
        }

        private static bool PathsEqual(string left, string right) =>
            Path.GetFullPath(left).Equals(
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
    }
}
