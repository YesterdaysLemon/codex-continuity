using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class UpdateStateTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-update-state-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("0.3.0", "0.3.0", true, "active")]
    [InlineData("0.2.0", "0.3.0", true, "staged")]
    [InlineData("0.2.0", "0.2.0", true, "deferred")]
    [InlineData("0.2.0", "0.2.0", false, "observed")]
    [InlineData("0.4.0", "0.4.0", false, "ahead")]
    public void LatestStateSeparatesRunningSelectedAndObservedVersions(
        string runningVersion,
        string selectedVersion,
        bool staged,
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
            "0.3.0",
            null,
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
    public void StoreBoundsHistoryAndToleratesMalformedState()
    {
        var now = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        var releases = Enumerable.Range(1, 40).Select(index =>
            new TrackedContinuityRelease(
                $"1.0.{index}",
                now.AddMinutes(index),
                now.AddMinutes(index),
                StagedAtUtc: null,
                AppliedAtUtc: null,
                LastError: null)).ToList();
        var store = Store();
        store.Save(new ContinuityUpdateState(
            1,
            now,
            now,
            "1.0.0",
            "1.0.0",
            "1.0.0",
            "1.0.40",
            null,
            releases));

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(32, loaded.ObservedCount);
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
