using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class UpdateStateTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-update-state-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("1.0.0", "1.0.0-rc.1", 1)]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.2", 1)]
    [InlineData("1.0.0-alpha", "1.0.0-1", 1)]
    [InlineData("1.0.0+windows", "1.0.0+linux", 0)]
    public void SemanticVersionComparisonUsesPrecedenceRules(
        string first,
        string second,
        int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(ContinuitySemanticVersion.Compare(first, second)));
    }

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
                LastError: null,
                StagedExecutableSha256: staged ? new string('b', 64) : null,
                RollbackExecutableSha256: staged ? new string('a', 64) : null)],
            RunningExecutableSha256: new string('b', 64));

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
            Releases: [],
            RunningExecutableSha256: new string('a', 64));

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

        var loadResult = store.Load();
        var loaded = loadResult.State;

        Assert.Equal(ContinuityUpdateStateLoadKind.Loaded, loadResult.Kind);
        Assert.NotNull(loaded);
        Assert.Equal(32, loaded.Releases.Count);
        Assert.Equal(40, loaded.ObservedCount);
        Assert.Equal(35, loaded.StagedCount);
        Assert.Equal(34, loaded.AppliedCount);
        Assert.Equal("1.0.40", loaded.Releases[0].Version);

        File.WriteAllText(Path.Combine(root, "update-status.json"), "not json");
        Assert.Equal(ContinuityUpdateStateLoadKind.Invalid, store.Load().Kind);

        File.WriteAllText(Path.Combine(root, "update-status.json"), "{}");
        Assert.Equal(ContinuityUpdateStateLoadKind.Invalid, store.Load().Kind);
    }

    [Fact]
    public void StoreDistinguishesMissingInvalidAndUnsupportedState()
    {
        var store = Store();
        var statePath = Path.Combine(root, "update-status.json");

        Assert.Equal(ContinuityUpdateStateLoadKind.Missing, store.Load().Kind);

        File.WriteAllText(statePath, "{\"schemaVersion\":2}");
        Assert.Equal(ContinuityUpdateStateLoadKind.UnsupportedSchema, store.Load().Kind);

        var now = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        store.Save(new ContinuityUpdateState(
            1,
            now,
            now,
            "1.0.0",
            "1.0.0",
            "1.0.0",
            true,
            null,
            null,
            0,
            0,
            0,
            Releases: []));
        File.WriteAllText(
            statePath,
            File.ReadAllText(statePath).PadRight((1024 * 1024) + 1));
        Assert.Equal(ContinuityUpdateStateLoadKind.Invalid, store.Load().Kind);
    }

    [Fact]
    public void StorePreservesStateWhenBoundedFieldsExceedThePersistedByteLimit()
    {
        var now = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        var store = new ContinuityUpdateStateStore(
            Path.Combine(root, "update-status.json"),
            retainedReleases: 256);
        var baseline = new ContinuityUpdateState(
            1,
            now,
            now,
            "1.0.0",
            "1.0.0",
            "1.0.0",
            true,
            null,
            null,
            0,
            0,
            0,
            Releases: []);
        store.Save(baseline);
        var path = Path.Combine(root, "update-status.json");
        var persisted = File.ReadAllBytes(path);
        var largeError = new string('\u754c', 2048);
        var releases = Enumerable.Range(1, 256).Select(index =>
            new TrackedContinuityRelease(
                $"1.0.{index}",
                now,
                now,
                StagedAtUtc: null,
                AppliedAtUtc: null,
                LastError: largeError)).ToList();

        Assert.Throws<InvalidDataException>(() => store.Save(baseline with
        {
            Releases = releases,
        }));
        Assert.Equal(persisted, File.ReadAllBytes(path));
    }

    [Fact]
    public void StoreAtomicallyPublishesACompleteReplacement()
    {
        var now = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        var state = new ContinuityUpdateState(
            1,
            now,
            now,
            "1.0.0",
            "1.0.0",
            "1.0.0",
            true,
            null,
            null,
            0,
            0,
            0,
            Releases: []);
        var store = Store();
        store.Save(state);
        var path = Path.Combine(root, "update-status.json");
        var previousBytes = File.ReadAllBytes(path);
        using var previousSnapshot = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var replacement = state with { LastCheckedAtUtc = now.AddMinutes(1) };

        store.Save(replacement);

        var observedPrevious = new byte[previousBytes.Length];
        previousSnapshot.ReadExactly(observedPrevious);
        Assert.Equal(previousBytes, observedPrevious);
        Assert.Equivalent(replacement, store.Load().State, strict: true);
        Assert.Empty(Directory.EnumerateFiles(root, "update-status.json.tmp-*"));
    }

    [Fact]
    public void StoreRoundTripsValidSemanticVersionsAndHistoricalDefaults()
    {
        var now = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        var state = new ContinuityUpdateState(
            1,
            now,
            now,
            "1.0.0",
            "1.1.0-rc.1",
            "1.1.0-rc.1",
            true,
            "1.1.0-rc.1+windows",
            null,
            1,
            1,
            0,
            [new TrackedContinuityRelease(
                "1.1.0-rc.1+windows",
                now,
                now,
                now,
                AppliedAtUtc: null,
                LastError: null)]);
        var store = Store();

        store.Save(state);
        var loaded = store.Load();

        Assert.Equal(ContinuityUpdateStateLoadKind.Loaded, loaded.Kind);
        Assert.Equivalent(state, loaded.State, strict: true);

        var statePath = Path.Combine(root, "update-status.json");
        File.WriteAllText(
            statePath,
            File.ReadAllText(statePath).Replace(
                "  \"runningProcessObserved\": true," + Environment.NewLine,
                string.Empty,
                StringComparison.Ordinal));
        var migrated = store.Load();
        Assert.Equal(ContinuityUpdateStateLoadKind.Loaded, migrated.Kind);
        Assert.False(migrated.State!.RunningProcessObserved);
    }

    [Theory]
    [InlineData("01.2.3")]
    [InlineData("1.2")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3+")]
    public void StoreRejectsInvalidSemanticVersions(string invalidVersion)
    {
        var now = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        var state = new ContinuityUpdateState(
            1,
            now,
            now,
            invalidVersion,
            "1.0.0",
            "1.0.0",
            true,
            null,
            null,
            0,
            0,
            0,
            Releases: []);

        Assert.Throws<ArgumentException>(() => Store().Save(state));
    }

    [Fact]
    public void StoreRejectsUnboundedErrorText()
    {
        var now = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        var state = new ContinuityUpdateState(
            1,
            now,
            now,
            "1.0.0",
            "1.0.0",
            "1.0.0",
            true,
            null,
            new string('x', 2049),
            0,
            0,
            0,
            Releases: []);

        Assert.Throws<ArgumentException>(() => Store().Save(state));
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
