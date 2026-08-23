using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class StagedSupervisorTransitionTests
{
    [Fact]
    public async Task LoadsExactRunningSelectedAndRollbackBuildsFromIndependentState()
    {
        using var fixture = new TransitionFixture();

        Assert.Equal(
            new StagedSupervisorTransitionLoadResult(
                StagedSupervisorTransitionLoadKind.Loaded,
                new StagedSupervisorTransitionBuilds(
                    fixture.Rollback,
                    fixture.Selected,
                    fixture.Rollback)),
            await fixture.LoadAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("unsupportedSchema")]
    [InlineData("inactive")]
    [InlineData("missingRollback")]
    [InlineData("oversized")]
    public async Task UnavailableInstallStateFailsClosed(string coordinate)
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
            case "oversized":
                File.WriteAllBytes(path, new byte[(512 * 1024) + 1]);
                break;
            default:
                throw new InvalidOperationException($"Unknown install coordinate {coordinate}.");
        }

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.InstallStateUnavailable,
            (await fixture.LoadAsync()).Kind);
    }

    [Fact]
    public async Task SelectedBuildMustBeIndependentlyVerified()
    {
        using var fixture = new TransitionFixture
        {
            ResolvedSelected = null,
        };

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.SelectedBuildUnavailable,
            (await fixture.LoadAsync()).Kind);
    }

    [Fact]
    public async Task SelectedResolutionIsBoundToTheCapturedInstallStatePath()
    {
        using var fixture = new TransitionFixture();
        var replacement = fixture.CreateBuild("0.5.0", 'c');
        fixture.ResolveExecutable = _ =>
        {
            fixture.InstallState = fixture.InstallState with
            {
                InstalledExecutable = replacement.Executable,
                BinarySha256 = replacement.ExecutableSha256,
            };
            fixture.SaveInstallState();
            return replacement;
        };

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.SelectedBuildUnavailable,
            (await fixture.LoadAsync()).Kind);
    }

    [Theory]
    [InlineData("filename")]
    [InlineData("contentDirectory")]
    [InlineData("versionsParent")]
    public async Task SelectedBuildMustUseEveryContentAddressedPathCoordinate(
        string coordinate)
    {
        using var fixture = new TransitionFixture();
        var versionDirectory = Path.GetDirectoryName(fixture.Selected.Executable)!;
        var executable = coordinate switch
        {
            "filename" => Path.Combine(versionDirectory, "not-continuity.exe"),
            "contentDirectory" => Path.Combine(
                ContinuityPaths.VersionsDirectory(fixture.Root),
                $"{fixture.Selected.Version}-{new string('c', 12)}",
                "CodexContinuity.exe"),
            "versionsParent" => Path.Combine(
                fixture.Root,
                "not-versions",
                Path.GetFileName(versionDirectory),
                "CodexContinuity.exe"),
            _ => throw new InvalidOperationException($"Unknown path coordinate {coordinate}."),
        };
        fixture.ResolvedSelected = fixture.Selected with { Executable = executable };
        fixture.InstallState = fixture.InstallState with { InstalledExecutable = executable };
        fixture.SaveInstallState();

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.SelectedBuildUnavailable,
            (await fixture.LoadAsync()).Kind);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    public async Task UnavailableUpdateStateFailsClosed(string coordinate)
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
            (await fixture.LoadAsync()).Kind);
    }

    [Theory]
    [InlineData("runningUnobserved")]
    [InlineData("sameVersion")]
    [InlineData("missingStagedTime")]
    [InlineData("alreadyApplied")]
    [InlineData("selectedDigest")]
    [InlineData("duplicateRelease")]
    [InlineData("selectedIdentityVersion")]
    public async Task PendingSelectedReleaseMustMatchEveryIndependentCoordinate(string coordinate)
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
            case "duplicateRelease":
                fixture.UpdateState = fixture.UpdateState with { Releases = [release, release] };
                break;
            case "selectedIdentityVersion":
                fixture.ResolvedSelected = fixture.CreateBuild("0.5.0", 'b');
                fixture.InstallState = fixture.InstallState with
                {
                    InstalledExecutable = fixture.ResolvedSelected.Executable,
                };
                fixture.SaveInstallState();
                break;
            default:
                throw new InvalidOperationException($"Unknown update coordinate {coordinate}.");
        }
        fixture.SaveUpdateState();

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.NoPendingStagedUpdate,
            (await fixture.LoadAsync()).Kind);
    }

    [Fact]
    public async Task SelectedBuildMustMatchTheCapturedInstallDigest()
    {
        using var fixture = new TransitionFixture();
        fixture.InstallState = fixture.InstallState with
        {
            BinarySha256 = new string('c', 64),
        };
        fixture.SaveInstallState();

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.SelectedBuildUnavailable,
            (await fixture.LoadAsync()).Kind);
    }

    [Theory]
    [InlineData("missingExecutable")]
    [InlineData("version")]
    [InlineData("runningDigest")]
    [InlineData("rollbackDigest")]
    public async Task RollbackMustMatchTheRunningBuildAndStagedLedger(string coordinate)
    {
        using var fixture = new TransitionFixture();
        var release = fixture.UpdateState.Releases.Single();
        switch (coordinate)
        {
            case "missingExecutable":
                fixture.ResolvedRollback = null;
                break;
            case "version":
                fixture.ResolvedRollback = fixture.CreateBuild("9.0.0", 'a');
                fixture.InstallState = fixture.InstallState with
                {
                    PreviousInstalledExecutable = fixture.ResolvedRollback.Executable,
                };
                fixture.SaveInstallState();
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
            (await fixture.LoadAsync()).Kind);
    }

    [Fact]
    public async Task RollbackBuildMustUseItsContentAddressedContinuityPath()
    {
        using var fixture = new TransitionFixture();
        fixture.ResolvedRollback = fixture.Rollback with
        {
            Executable = Path.Combine(fixture.Root, "CodexContinuity.exe"),
        };

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.RollbackBuildUnavailable,
            (await fixture.LoadAsync()).Kind);
    }

    [Fact]
    public async Task CoherentStagedDowngradeIsNotAPendingUpdate()
    {
        using var fixture = new TransitionFixture(selectedVersion: "0.2.0");

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.NoPendingStagedUpdate,
            (await fixture.LoadAsync()).Kind);
    }

    [Fact]
    public async Task PublisherVerificationBindsRollbackAndSelectedExecutables()
    {
        using var fixture = new TransitionFixture();
        string? trusted = null;
        IReadOnlyList<string>? candidates = null;
        fixture.VerifyMatchingPublisher = (observedTrusted, observedCandidates, _) =>
        {
            trusted = observedTrusted;
            candidates = observedCandidates;
            return Task.CompletedTask;
        };

        Assert.Equal(StagedSupervisorTransitionLoadKind.Loaded, (await fixture.LoadAsync()).Kind);
        Assert.Equal(fixture.Rollback.Executable, trusted);
        Assert.Equal([fixture.Selected.Executable], candidates);
    }

    [Fact]
    public async Task UntrustedPublisherFailsClosed()
    {
        using var fixture = new TransitionFixture
        {
            VerifyMatchingPublisher = (_, _, _) =>
                Task.FromException(new InvalidDataException("untrusted")),
        };

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.PublisherVerificationUnavailable,
            (await fixture.LoadAsync()).Kind);
    }

    [Fact]
    public async Task BuildChangedDuringPublisherVerificationFailsClosed()
    {
        using var fixture = new TransitionFixture();
        fixture.VerifyMatchingPublisher = (_, _, _) =>
        {
            fixture.ResolvedSelected = fixture.Selected with
            {
                ExecutableSha256 = new string('c', 64),
            };
            return Task.CompletedTask;
        };

        Assert.Equal(
            StagedSupervisorTransitionLoadKind.SelectedBuildUnavailable,
            (await fixture.LoadAsync()).Kind);
    }

    private sealed class TransitionFixture : IDisposable
    {
        internal static readonly DateTimeOffset Now =
            DateTimeOffset.Parse("2026-08-23T12:00:00Z");

        internal TransitionFixture(string selectedVersion = "0.4.0")
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"codex-continuity-staged-transition-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Rollback = Build("0.3.0", 'a');
            Selected = Build(selectedVersion, 'b');
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
            ResolvedSelected = Selected;
            ResolvedRollback = Rollback;
            SaveInstallState();
            SaveUpdateState();
        }

        internal string Root { get; }
        internal SupervisorExecutableIdentity Selected { get; }
        internal SupervisorExecutableIdentity Rollback { get; }
        internal InstallState InstallState { get; set; }
        internal ContinuityUpdateState UpdateState { get; set; }
        internal SupervisorExecutableIdentity? ResolvedSelected { get; set; }
        internal SupervisorExecutableIdentity? ResolvedRollback { get; set; }
        internal Func<string, SupervisorExecutableIdentity?>? ResolveExecutable { get; set; }
        internal Func<string, IReadOnlyList<string>, CancellationToken, Task>?
            VerifyMatchingPublisher
        { get; set; }

        internal Task<StagedSupervisorTransitionLoadResult> LoadAsync() =>
            StagedSupervisorTransitionReader.LoadAsync(
                Root,
                new StagedSupervisorTransitionChecks(
                    executable => ResolveExecutable?.Invoke(executable) ??
                        (PathsEqual(executable, InstallState.InstalledExecutable)
                            ? ResolvedSelected
                            : InstallState.PreviousInstalledExecutable is { } rollbackExecutable &&
                              PathsEqual(executable, rollbackExecutable)
                                ? ResolvedRollback
                                : null),
                    (trusted, candidates, cancellationToken) =>
                        VerifyMatchingPublisher?.Invoke(
                            trusted,
                            candidates,
                            cancellationToken) ?? Task.CompletedTask));

        internal void SaveInstallState() => new InstallStateStore(
            ContinuityPaths.InstallStateFile(Root)).Save(InstallState);

        internal void SaveUpdateState() => new ContinuityUpdateStateStore(
            ContinuityPaths.UpdateStatusFile(Root)).Save(UpdateState);

        public void Dispose() => Directory.Delete(Root, recursive: true);

        internal SupervisorExecutableIdentity CreateBuild(
            string version,
            char sha256Character)
        {
            var sha256 = new string(sha256Character, 64);
            return new(
                version,
                ContinuityPaths.VersionedSupervisorExecutable(Root, version, sha256),
                sha256);
        }

        private SupervisorExecutableIdentity Build(
            string version,
            char sha256Character) => CreateBuild(version, sha256Character);

        private static bool PathsEqual(string left, string right) =>
            Path.GetFullPath(left).Equals(
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
    }
}
