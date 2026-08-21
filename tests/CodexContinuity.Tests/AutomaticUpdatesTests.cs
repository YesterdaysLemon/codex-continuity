using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class AutomaticUpdatesTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-update-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ReleaseFeedFiltersNonStableReleasesAndRetainsRequiredAssets()
    {
        const string json =
            """
            [
              { "tag_name": "v0.3.0", "draft": false, "prerelease": false,
                "published_at": "2026-08-21T12:00:00Z", "assets": [
                  { "name": "CodexContinuity-win-x64.zip",
                    "browser_download_url": "https://github.com/YesterdaysLemon/codex-continuity/releases/download/v0.3.0/CodexContinuity-win-x64.zip" },
                  { "name": "CodexContinuity-win-x64.zip.sha256",
                    "browser_download_url": "https://github.com/YesterdaysLemon/codex-continuity/releases/download/v0.3.0/CodexContinuity-win-x64.zip.sha256" }
                ] },
              { "tag_name": "v0.2.1", "draft": false, "prerelease": false,
                "published_at": "2026-08-20T12:00:00Z", "assets": [
                  { "name": "CodexContinuity-win-x64.zip",
                    "browser_download_url": "https://attacker.example/archive" }
                ] },
              { "tag_name": "v0.4.0", "draft": false, "prerelease": true,
                "published_at": "2026-08-22T12:00:00Z", "assets": [] },
              { "draft": false, "assets": "malformed" }
            ]
            """;

        var releases = GitHubReleaseFeed.Parse(json);

        Assert.Equal(
            [
                new PublishedContinuityRelease(
                    "0.3.0",
                    DateTimeOffset.Parse("2026-08-21T12:00:00Z"),
                    "https://github.com/YesterdaysLemon/codex-continuity/releases/download/v0.3.0/CodexContinuity-win-x64.zip",
                    "https://github.com/YesterdaysLemon/codex-continuity/releases/download/v0.3.0/CodexContinuity-win-x64.zip.sha256"),
                new PublishedContinuityRelease(
                    "0.2.1",
                    DateTimeOffset.Parse("2026-08-20T12:00:00Z"),
                    null,
                    null),
            ],
            releases);
    }

    [Fact]
    public void ReleaseFeedRejectsOversizedResponses()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            GitHubReleaseFeed.Parse(new string(' ', 1024 * 1024 + 1)));

        Assert.Contains("exceeds the updater limit", exception.Message);
    }

    [Fact]
    public async Task StagesLatestReleaseAndMarksItActiveOnlyOnLaterSupervisorStart()
    {
        var now = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        var releases = new[]
        {
            Release("0.3.0", "2026-08-21T12:00:00Z"),
            Release("0.2.0", "2026-08-21T11:00:00Z"),
        };
        var staged = new List<string>();
        var store = Store();
        var coordinator = new AutomaticUpdateCoordinator(
            store,
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(releases),
            release =>
            {
                staged.Add(release.Version);
                return Task.FromResult(StagedBuild());
            },
            () => now);

        var first = await coordinator.CheckAndStageAsync(
            Build("0.1.0"), Build("0.1.0"), runningProcessObserved: true, CancellationToken.None);

        Assert.Equal(["0.3.0"], staged);
        Assert.Equal(2, first.ObservedCount);
        Assert.Equal(1, first.StagedCount);
        Assert.Equal(0, first.AppliedCount);
        Assert.Equal("staged", first.LatestState);

        now = now.AddMinutes(1);
        var repaired = new AutomaticUpdateCoordinator(
            store,
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(releases),
            release =>
            {
                staged.Add(release.Version);
                return Task.FromResult(StagedBuild());
            },
            () => now);

        var restaged = await repaired.CheckAndStageAsync(
            Build("0.1.0"), selectedBuild: null, runningProcessObserved: true, CancellationToken.None);

        Assert.Equal(["0.3.0", "0.3.0"], staged);
        Assert.Equal("staged", restaged.LatestState);

        now = now.AddMinutes(1);
        var rolledBack = new AutomaticUpdateCoordinator(
            store,
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(releases),
            _ => throw new InvalidOperationException("A rolled-back release must not restage."),
            () => now);

        var deferred = await rolledBack.CheckAndStageAsync(
            Build("0.1.0"), Build("0.1.0"), runningProcessObserved: true, CancellationToken.None);

        Assert.Equal("0.1.0", deferred.SelectedVersion);
        Assert.Equal("deferred", deferred.LatestState);

        now = now.AddMinutes(1);
        var restarted = new AutomaticUpdateCoordinator(
            store,
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(releases),
            _ => throw new InvalidOperationException("An already staged release must not restage."),
            () => now);

        var second = await restarted.CheckAndStageAsync(
            Build("0.3.0", 'b'),
            Build("0.3.0", 'b'),
            runningProcessObserved: true,
            CancellationToken.None);

        Assert.Equal(1, second.StagedCount);
        Assert.Equal(1, second.AppliedCount);
        Assert.Equal("active", second.LatestState);
        Assert.Null(second.LastError);

        now = now.AddMinutes(1);
        var unavailable = new AutomaticUpdateCoordinator(
            store,
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(releases),
            _ => throw new InvalidOperationException("The active release must not restage."),
            () => now);

        var stopped = await unavailable.CheckAndStageAsync(
            Build("0.3.0", 'b'),
            Build("0.3.0", 'b'),
            runningProcessObserved: false,
            CancellationToken.None);

        Assert.False(stopped.RunningProcessObserved);
        Assert.Equal(1, stopped.AppliedCount);
        Assert.Equal("inactive", stopped.LatestState);
    }

    [Fact]
    public async Task RestagesAnUnprovenAlreadySelectedLatestRelease()
    {
        var staged = 0;
        var coordinator = new AutomaticUpdateCoordinator(
            Store(),
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(
                [Release("0.3.0", "2026-08-21T12:00:00Z")]),
            _ =>
            {
                staged++;
                return Task.FromResult(StagedBuild());
            },
            () => DateTimeOffset.Parse("2026-08-21T13:00:00Z"));

        var state = await coordinator.CheckAndStageAsync(
            Build("0.2.0"), Build("0.3.0", 'b'), runningProcessObserved: true, CancellationToken.None);

        Assert.Equal(1, staged);
        Assert.Equal(1, state.StagedCount);
        Assert.Equal("0.3.0", state.SelectedVersion);
        Assert.Equal("staged", state.LatestState);
        Assert.Null(state.LastError);
    }

    [Fact]
    public async Task RetainsASelectedReleaseOnlyWhenItsPersistedDigestMatches()
    {
        var now = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        var store = Store();
        store.Save(StagedState(now));
        var coordinator = new AutomaticUpdateCoordinator(
            store,
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(
                [Release("0.3.0", "2026-08-21T12:00:00Z")]),
            _ => throw new InvalidOperationException("A verified selection must not redownload."),
            () => now.AddMinutes(1));

        var state = await coordinator.CheckAndStageAsync(
            Build("0.2.0"),
            Build("0.3.0", 'b'),
            runningProcessObserved: true,
            CancellationToken.None);

        Assert.Equal("staged", state.LatestState);
        Assert.Null(state.LastError);
    }

    [Fact]
    public async Task RunningLatestWithMissingSelectionIsRepaired()
    {
        var staged = 0;
        var coordinator = new AutomaticUpdateCoordinator(
            Store(),
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(
                [Release("0.3.0", "2026-08-21T12:00:00Z")]),
            _ =>
            {
                staged++;
                return Task.FromResult(StagedBuild());
            },
            () => DateTimeOffset.Parse("2026-08-21T13:00:00Z"));

        var state = await coordinator.CheckAndStageAsync(
            Build("0.3.0", 'b'),
            selectedBuild: null,
            runningProcessObserved: true,
            CancellationToken.None);

        Assert.Equal(1, staged);
        Assert.Equal("0.3.0", state.SelectedVersion);
        Assert.Equal("active", state.LatestState);
    }

    [Fact]
    public async Task ActiveApplicationRequiresTheStagedExecutableDigest()
    {
        var now = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        var store = Store();
        store.Save(StagedState(now));
        var coordinator = new AutomaticUpdateCoordinator(
            store,
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(
                [Release("0.3.0", "2026-08-21T12:00:00Z")]),
            _ => throw new InvalidOperationException("A rolled-back selection must remain deferred."),
            () => now.AddMinutes(1));

        var state = await coordinator.CheckAndStageAsync(
            Build("0.3.0", 'c'),
            Build("0.2.0"),
            runningProcessObserved: true,
            CancellationToken.None);

        Assert.Equal(0, state.AppliedCount);
        Assert.Equal("deferred", state.LatestState);
    }

    [Fact]
    public async Task MissingRollbackProofLeavesStagingFailed()
    {
        var coordinator = new AutomaticUpdateCoordinator(
            Store(),
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(
                [Release("0.3.0", "2026-08-21T12:00:00Z")]),
            _ => Task.FromResult(new StagedContinuityBuild(new string('b', 64), "invalid")),
            () => DateTimeOffset.Parse("2026-08-21T13:00:00Z"));

        var state = await coordinator.CheckAndStageAsync(
            Build("0.2.0"),
            Build("0.2.0"),
            runningProcessObserved: true,
            CancellationToken.None);

        Assert.Equal(0, state.StagedCount);
        Assert.Equal("failed", state.LatestState);
        Assert.Contains("rollback executable digests", state.LastError);
    }

    [Fact]
    public async Task MissingReleaseAssetsRemainVisibleAsAStagingFailure()
    {
        var release = new PublishedContinuityRelease(
            "0.2.0",
            DateTimeOffset.Parse("2026-08-21T12:00:00Z"),
            ArchiveUrl: null,
            ChecksumUrl: null);
        var coordinator = new AutomaticUpdateCoordinator(
            Store(),
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>([release]),
            _ => throw new InvalidOperationException("Staging should not run without assets."),
            () => DateTimeOffset.Parse("2026-08-21T13:00:00Z"));

        var state = await coordinator.CheckAndStageAsync(
            Build("0.1.0"), Build("0.1.0"), runningProcessObserved: true, CancellationToken.None);

        Assert.Equal(1, state.ObservedCount);
        Assert.Equal(0, state.StagedCount);
        Assert.Equal("failed", state.LatestState);
        Assert.Contains("missing the Windows archive", state.LastError);
    }

    [Fact]
    public async Task PersistedUpdateErrorsAreSingleLineAndBounded()
    {
        var coordinator = new AutomaticUpdateCoordinator(
            Store(),
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(
                [Release("0.2.0", "2026-08-21T12:00:00Z")]),
            _ => throw new InvalidOperationException($"failure\r\n{new string('x', 2_000)}"),
            () => DateTimeOffset.Parse("2026-08-21T13:00:00Z"));

        var state = await coordinator.CheckAndStageAsync(
            Build("0.1.0"), Build("0.1.0"), runningProcessObserved: true, CancellationToken.None);

        Assert.NotNull(state.LastError);
        Assert.Equal(1_001, state.LastError.Length);
        Assert.DoesNotContain('\r', state.LastError);
        Assert.DoesNotContain('\n', state.LastError);
        Assert.EndsWith("…", state.LastError);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private ContinuityUpdateStateStore Store()
    {
        Directory.CreateDirectory(root);
        return new ContinuityUpdateStateStore(Path.Combine(root, "update-status.json"));
    }

    private static PublishedContinuityRelease Release(string version, string publishedAt) => new(
        version,
        DateTimeOffset.Parse(publishedAt),
        $"https://example.test/v{version}/archive",
        $"https://example.test/v{version}/checksum");

    private static ContinuityBuildIdentity Build(string version, char hash = 'a') =>
        new(version, new string(hash, 64));

    private static StagedContinuityBuild StagedBuild(
        char selectedHash = 'b',
        char rollbackHash = 'a') =>
        new(new string(selectedHash, 64), new string(rollbackHash, 64));

    private static ContinuityUpdateState StagedState(DateTimeOffset now) => new(
        1,
        now,
        now,
        "0.2.0",
        "0.2.0",
        "0.3.0",
        true,
        "0.3.0",
        null,
        1,
        1,
        0,
        [new TrackedContinuityRelease(
            "0.3.0",
            now,
            now,
            now,
            AppliedAtUtc: null,
            LastError: null,
            StagedExecutableSha256: new string('b', 64),
            RollbackExecutableSha256: new string('a', 64))],
        RunningExecutableSha256: new string('a', 64));
}
