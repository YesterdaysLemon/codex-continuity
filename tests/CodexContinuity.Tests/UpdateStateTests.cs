using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class UpdateStateTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-update-state-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("0.3.0", "0.3.0", true, true, "active")]
    [InlineData("0.3.0", "0.3.0", false, false, "inactive")]
    [InlineData("0.2.0", "0.3.0", true, true, "staged")]
    [InlineData("0.2.0", "0.2.0", true, true, "deferred")]
    [InlineData("0.2.0", "0.2.0", false, true, "observed")]
    [InlineData("0.4.0", "0.4.0", false, true, "ahead")]
    public void LatestStateSeparatesRunningSelectedAndObservedVersions(
        string runningVersion,
        string selectedVersion,
        bool staged,
        bool runningProcessObserved,
        string expected)
    {
        var now = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        var state = new ContinuityUpdateState(
            1,
            now,
            now,
            "0.2.0",
            runningVersion,
            selectedVersion,
            runningProcessObserved,
            "0.3.0",
            null,
            1,
            staged ? 1 : 0,
            0,
            [new TrackedContinuityRelease(
                "0.3.0",
                now,
                now,
                staged ? now : null,
                AppliedAtUtc: null,
                LastError: null)]);

        Assert.Equal(expected, state.LatestState);
    }

    [Fact]
    public void LatestStateIsActiveWhenBaselineAlreadyMatchesLatestRelease()
    {
        var now = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        var state = new ContinuityUpdateState(
            1,
            now,
            now,
            "0.3.0",
            "0.3.0",
            "0.3.0",
            true,
            "0.3.0",
            null,
            0,
            0,
            0,
            Releases: []);

        Assert.Equal("active", state.LatestState);
    }

    [Fact]
    public void StoreBoundsHistoryAndToleratesMalformedState()
    {
        var now = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        var releases = Enumerable.Range(1, 40).Select(index =>
            new TrackedContinuityRelease(
                $"1.0.{index}",
                now.AddMinutes(index),
                now.AddMinutes(index),
                StagedAtUtc: index <= 35 ? now : null,
                AppliedAtUtc: index <= 34 ? now : null,
                LastError: null)).ToList();
        var store = Store();
        store.Save(new ContinuityUpdateState(
            1,
            now,
            now,
            "1.0.0",
            "1.0.0",
            "1.0.0",
            true,
            "1.0.40",
            null,
            40,
            35,
            34,
            releases));

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(32, loaded.Releases.Count);
        Assert.Equal(40, loaded.ObservedCount);
        Assert.Equal(35, loaded.StagedCount);
        Assert.Equal(34, loaded.AppliedCount);
        Assert.Equal("1.0.40", loaded.Releases[0].Version);

        File.WriteAllText(Path.Combine(root, "update-status.json"), "not json");
        Assert.Null(store.Load());

        File.WriteAllText(Path.Combine(root, "update-status.json"), "{}");
        Assert.Null(store.Load());
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
}
