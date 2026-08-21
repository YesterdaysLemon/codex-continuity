using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class AutomaticUpdatesTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-update-tests-{Guid.NewGuid():N}");

    [Fact]
    public void MissingSelectedExecutableIsTreatedAsUnselected()
    {
        Assert.Equal(
            "0.0.0",
            AutomaticUpdateRunner.ResolveSelectedVersion(Path.Combine(root, "missing.exe")));
    }

    [Fact]
    public void ReleaseFeedFiltersNonStableReleasesAndRetainsRequiredAssets()
    {
        const string json =
            """
            [
              { "tag_name": "v0.3.0", "draft": false, "prerelease": false,
                "published_at": "2026-08-21T12:00:00Z", "assets": [
                  { "name": "CodexContinuity-win-x64.zip",
                    "browser_download_url": "https://example.test/v0.3.0/archive" },
                  { "name": "CodexContinuity-win-x64.zip.sha256",
                    "browser_download_url": "https://example.test/v0.3.0/checksum" }
                ] },
              { "tag_name": "v0.2.1", "draft": false, "prerelease": false,
                "published_at": "2026-08-20T12:00:00Z", "assets": [] },
              { "tag_name": "v0.4.0", "draft": false, "prerelease": true,
                "published_at": "2026-08-22T12:00:00Z", "assets": [] }
            ]
            """;

        var releases = GitHubReleaseFeed.Parse(json);

        Assert.Equal(
            [
                new PublishedContinuityRelease(
                    "0.3.0",
                    DateTimeOffset.Parse("2026-08-21T12:00:00Z"),
                    "https://example.test/v0.3.0/archive",
                    "https://example.test/v0.3.0/checksum"),
                new PublishedContinuityRelease(
                    "0.2.1",
                    DateTimeOffset.Parse("2026-08-20T12:00:00Z"),
                    null,
                    null),
            ],
            releases);
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
                return Task.CompletedTask;
            },
            () => now);

        var first = await coordinator.CheckAndStageAsync(
            "0.1.0",
            "0.1.0",
            CancellationToken.None);

        Assert.Equal(["0.3.0"], staged);
        Assert.Equal(2, first.ObservedCount);
        Assert.Equal(1, first.StagedCount);
        Assert.Equal(0, first.AppliedCount);
        Assert.Equal("staged", first.LatestState);

        now = now.AddMinutes(1);
        var rolledBack = new AutomaticUpdateCoordinator(
            store,
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(releases),
            _ => throw new InvalidOperationException("A rolled-back release must not restage."),
            () => now);

        var deferred = await rolledBack.CheckAndStageAsync(
            "0.1.0",
            "0.1.0",
            CancellationToken.None);

        Assert.Equal("0.1.0", deferred.SelectedVersion);
        Assert.Equal("deferred", deferred.LatestState);

        now = now.AddMinutes(1);
        var restarted = new AutomaticUpdateCoordinator(
            store,
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(releases),
            _ => throw new InvalidOperationException("An already staged release must not restage."),
            () => now);

        var second = await restarted.CheckAndStageAsync(
            "0.3.0",
            "0.3.0",
            CancellationToken.None);

        Assert.Equal(1, second.StagedCount);
        Assert.Equal(1, second.AppliedCount);
        Assert.Equal("active", second.LatestState);
        Assert.Null(second.LastError);
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
            "0.1.0",
            "0.1.0",
            CancellationToken.None);

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
            "0.1.0",
            "0.1.0",
            CancellationToken.None);

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
}
