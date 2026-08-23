using System.Text.Json.Nodes;
using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class HandoffPlanningTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-22T12:00:00Z");

    [Fact]
    public void ParsesActiveFlagsAndRejectsMalformedOrUnexpectedShapes()
    {
        Assert.Equivalent(
            new ThreadLifecycleStatus(
                "active",
                ["waitingOnApproval", "waitingOnUserInput"],
                Malformed: false),
            ThreadLifecycleStatus.Parse(JsonNode.Parse(
                """{"type":"active","activeFlags":["waitingOnApproval","waitingOnUserInput"]}""")),
            strict: true);
        Assert.Equal(
            ThreadLifecycleStatus.Unknown(),
            ThreadLifecycleStatus.Parse(JsonNode.Parse("""{"type":12}""")));
        Assert.True(ThreadLifecycleStatus.Parse(JsonNode.Parse(
            """{"type":"active"}""")).Malformed);
        Assert.True(ThreadLifecycleStatus.Parse(JsonNode.Parse(
            """{"type":"idle","activeFlags":[]}""")).Malformed);
    }

    [Fact]
    public void QuiescentThreadsAreEligibleForHandoffWhenNoUpdateIsSelected()
    {
        var plan = ContinuityHandoffPlanner.Create(
            backendReady: true,
            [Status("idle"), Status("notLoaded")],
            LoadedUpdateState("0.3.0", "0.3.0"),
            LoadedSelectedBuild("0.3.0", 'b'));

        Assert.Equivalent(
            new ContinuityHandoffPlan(
                "handoff",
                TransitionReady: true,
                BackendReady: true,
                "loaded",
                PendingUpdate: false,
                ThreadCount: 2,
                new HandoffBlockerCounts(0, 0, 0, 0, 0),
                Reasons: []),
            plan,
            strict: true);
    }

    [Fact]
    public void QuiescentThreadsApplyAVerifiedSelectedUpdate()
    {
        var plan = ContinuityHandoffPlanner.Create(
            backendReady: true,
            [Status("idle")],
            LoadedUpdateState("0.3.0", "0.4.0", staged: true),
            LoadedSelectedBuild("0.4.0", 'a'));

        Assert.Equal("applyUpdate", plan.Action);
        Assert.True(plan.TransitionReady);
        Assert.True(plan.PendingUpdate);
        Assert.Empty(plan.Reasons);
    }

    [Fact]
    public void BusyThreadsTakePrecedenceOverAVerifiedSelectedUpdate()
    {
        var plan = ContinuityHandoffPlanner.Create(
            backendReady: true,
            [Status("active")],
            LoadedUpdateState("0.3.0", "0.4.0", staged: true),
            LoadedSelectedBuild("0.4.0", 'a'));

        Assert.Equal("wait", plan.Action);
        Assert.True(plan.PendingUpdate);
        Assert.Equal(["runningTurns"], plan.Reasons);
    }

    [Fact]
    public void EveryBusyOrUncertainStateBlocksAndIsCounted()
    {
        var plan = ContinuityHandoffPlanner.Create(
            backendReady: true,
            [
                Status("active"),
                Status("active", "waitingOnApproval"),
                Status("active", "waitingOnUserInput"),
                Status("active", "waitingOnApproval", "futureFlag"),
                Status("systemError"),
                Status("futureStatus"),
                ThreadLifecycleStatus.Unknown(),
            ],
            LoadedUpdateState("0.3.0", "0.3.0"),
            LoadedSelectedBuild("0.3.0", 'b'));

        Assert.Equal("wait", plan.Action);
        Assert.False(plan.TransitionReady);
        Assert.Equal(new HandoffBlockerCounts(1, 2, 1, 1, 3), plan.Blockers);
        Assert.Equal(
            [
                "runningTurns",
                "waitingOnApproval",
                "waitingOnUserInput",
                "systemError",
                "unknownThreadState",
            ],
            plan.Reasons);
    }

    [Theory]
    [InlineData(nameof(ContinuityUpdateStateLoadKind.Missing), "updateStateMissing")]
    [InlineData(nameof(ContinuityUpdateStateLoadKind.Invalid), "updateStateInvalid")]
    [InlineData(nameof(ContinuityUpdateStateLoadKind.UnsupportedSchema), "updateStateUnsupportedSchema")]
    [InlineData(nameof(ContinuityUpdateStateLoadKind.Unreadable), "updateStateUnreadable")]
    public void UnavailableUpdateStateFailsClosed(
        string kindName,
        string expectedReason)
    {
        var kind = Enum.Parse<ContinuityUpdateStateLoadKind>(kindName);
        var plan = ContinuityHandoffPlanner.Create(
            backendReady: true,
            [Status("idle")],
            new ContinuityUpdateStateLoadResult(kind, State: null),
            LoadedSelectedBuild("0.3.0", 'b'));

        Assert.Equal("wait", plan.Action);
        Assert.False(plan.PendingUpdate);
        Assert.Equal([expectedReason], plan.Reasons);
    }

    [Fact]
    public void BackendAndUnverifiedUpdateSelectionFailClosed()
    {
        var unverifiedSelection = ContinuityHandoffPlanner.Create(
            backendReady: false,
            [],
            LoadedUpdateState("0.3.0", "0.4.0", staged: false),
            LoadedSelectedBuild("0.4.0", 'a'));
        var staleRunningState = ContinuityHandoffPlanner.Create(
            backendReady: true,
            [],
            LoadedUpdateState("0.3.0", "0.3.0", runningProcessObserved: false),
            LoadedSelectedBuild("0.3.0", 'b'));

        Assert.Equal(
            ["backendUnavailable", "selectedUpdateUnverified"],
            unverifiedSelection.Reasons);
        Assert.Equal(["runningUpdateStateUnverified"], staleRunningState.Reasons);
    }

    [Theory]
    [InlineData(
        nameof(ContinuitySelectedBuildLoadKind.MissingInstallState),
        "selectedBuildMissingInstallState")]
    [InlineData(
        nameof(ContinuitySelectedBuildLoadKind.InactiveInstallState),
        "selectedBuildInactiveInstallState")]
    [InlineData(
        nameof(ContinuitySelectedBuildLoadKind.InvalidInstallState),
        "selectedBuildInvalidInstallState")]
    [InlineData(
        nameof(ContinuitySelectedBuildLoadKind.UnreadableInstallState),
        "selectedBuildUnreadableInstallState")]
    [InlineData(
        nameof(ContinuitySelectedBuildLoadKind.UnverifiedExecutable),
        "selectedBuildUnverifiedExecutable")]
    public void UnavailableSelectedBuildFailsClosed(
        string kindName,
        string expectedReason)
    {
        var kind = Enum.Parse<ContinuitySelectedBuildLoadKind>(kindName);
        var plan = ContinuityHandoffPlanner.Create(
            backendReady: true,
            [Status("idle")],
            LoadedUpdateState("0.3.0", "0.3.0"),
            new ContinuitySelectedBuildLoadResult(kind, Build: null));

        Assert.Equal("wait", plan.Action);
        Assert.Equal([expectedReason], plan.Reasons);
    }

    [Fact]
    public void SelectedBuildMustMatchTheUpdateLedger()
    {
        var unchangedMismatch = ContinuityHandoffPlanner.Create(
            backendReady: true,
            [Status("idle")],
            LoadedUpdateState("0.3.0", "0.3.0"),
            LoadedSelectedBuild("0.4.0", 'a'));
        var stagedMismatch = ContinuityHandoffPlanner.Create(
            backendReady: true,
            [Status("idle")],
            LoadedUpdateState("0.3.0", "0.4.0", staged: true),
            LoadedSelectedBuild("0.4.0", 'c'));

        Assert.Equal(["selectedBuildDoesNotMatchUpdateState"], unchangedMismatch.Reasons);
        Assert.Equal(["selectedUpdateUnverified"], stagedMismatch.Reasons);
    }

    [Fact]
    public void SelectedBuildReaderVerifiesThePersistedExecutableDigest()
    {
        var root = TemporaryDirectory();
        try
        {
            var executable = typeof(AutomaticUpdateRunner).Assembly.Location;
            var build = AutomaticUpdateRunner.ResolveBuildIdentity(executable);
            Assert.NotNull(build);
            var store = new InstallStateStore(ContinuityPaths.InstallStateFile(root));
            store.Save(InstallState(executable, build.ExecutableSha256));

            Assert.Equal(
                new ContinuitySelectedBuildLoadResult(
                    ContinuitySelectedBuildLoadKind.Loaded,
                    build),
                ContinuitySelectedBuildReader.Load(root));

            store.Save(InstallState(executable, build.ExecutableSha256) with
            {
                SchemaVersion = int.MaxValue,
            });
            Assert.Equal(
                ContinuitySelectedBuildLoadKind.InvalidInstallState,
                ContinuitySelectedBuildReader.Load(root).Kind);

            store.Save(InstallState(executable, new string('f', 64)));
            Assert.Equal(
                new ContinuitySelectedBuildLoadResult(
                    ContinuitySelectedBuildLoadKind.UnverifiedExecutable,
                    Build: null),
                ContinuitySelectedBuildReader.Load(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HandoffCommandLoadsStateAndWritesStableReadOnlyJson()
    {
        var root = TemporaryDirectory();
        try
        {
            var executable = typeof(AutomaticUpdateRunner).Assembly.Location;
            var build = AutomaticUpdateRunner.ResolveBuildIdentity(executable);
            Assert.NotNull(build);
            new InstallStateStore(ContinuityPaths.InstallStateFile(root)).Save(
                InstallState(executable, build.ExecutableSha256));
            new ContinuityUpdateStateStore(ContinuityPaths.UpdateStatusFile(root)).Save(
                LoadedUpdateState(
                    build.Version,
                    build.Version,
                    runningSha256: build.ExecutableSha256).State!);
            var installBefore = File.ReadAllBytes(ContinuityPaths.InstallStateFile(root));
            var updateBefore = File.ReadAllBytes(ContinuityPaths.UpdateStatusFile(root));
            using var output = new StringWriter();

            var exitCode = await Program.PrintHandoffPlanAsync(
                root,
                () => Task.FromResult(new ContinuityThreadSnapshot(
                    BackendReady: true,
                    Threads: [Status("idle")])),
                output);
            var json = JsonNode.Parse(output.ToString())!.AsObject();

            Assert.Equal(0, exitCode);
            Assert.Equal("handoff-plan", Program.ResolveCommand(
                setupExecutable: false,
                ["handoff-plan"]));
            Assert.Equal("handoff", json["action"]!.GetValue<string>());
            Assert.True(json["transitionReady"]!.GetValue<bool>());
            Assert.Equal("loaded", json["updateState"]!.GetValue<string>());
            Assert.Null(json["Action"]);
            Assert.Equal(
                installBefore,
                File.ReadAllBytes(ContinuityPaths.InstallStateFile(root)));
            Assert.Equal(updateBefore, File.ReadAllBytes(ContinuityPaths.UpdateStatusFile(root)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SelectedBuildReaderFailsClosedOnMissingOrMalformedState()
    {
        var root = TemporaryDirectory();
        try
        {
            Assert.Equal(
                ContinuitySelectedBuildLoadKind.MissingInstallState,
                ContinuitySelectedBuildReader.Load(root).Kind);

            File.WriteAllText(ContinuityPaths.InstallStateFile(root), "{");
            Assert.Equal(
                ContinuitySelectedBuildLoadKind.InvalidInstallState,
                ContinuitySelectedBuildReader.Load(root).Kind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ThreadLifecycleStatus Status(string type, params string[] activeFlags) =>
        new(type, activeFlags, Malformed: false);

    private static ContinuitySelectedBuildLoadResult LoadedSelectedBuild(
        string version,
        char sha256Character) => new(
            ContinuitySelectedBuildLoadKind.Loaded,
            new ContinuityBuildIdentity(version, new string(sha256Character, 64)));

    private static InstallState InstallState(string executable, string sha256) => new(
        SchemaVersion: 4,
        Port: 45123,
        InstalledExecutable: executable,
        PreviousInstalledExecutable: null,
        InstalledTrayExecutable: null,
        PreviousInstalledTrayExecutable: null,
        BinarySha256: sha256,
        AppServerUrl: new OwnedString(null, LoopbackEndpoint.WebSocketUrl(45123)),
        UpdaterSetting: new OwnedString(null, "false"),
        CommandPath: null,
        StartupCommand: new OwnedString(null, "fixture"),
        TrayStartupCommand: null,
        PreviousInstalledAppRegistration: null,
        InstalledAppRegistration: null,
        InstalledAtUtc: Now);

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"codex-continuity-handoff-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static ContinuityUpdateStateLoadResult LoadedUpdateState(
        string runningVersion,
        string selectedVersion,
        bool staged = false,
        bool runningProcessObserved = true,
        string? runningSha256 = null)
    {
        var releases = staged
            ? new TrackedContinuityRelease[]
            {
                new(
                    selectedVersion,
                    Now,
                    Now,
                    Now,
                    AppliedAtUtc: null,
                    LastError: null,
                    StagedExecutableSha256: new string('a', 64)),
            }
            : [];
        return new ContinuityUpdateStateLoadResult(
            ContinuityUpdateStateLoadKind.Loaded,
            new ContinuityUpdateState(
                1,
                Now,
                Now,
                runningVersion,
                runningVersion,
                selectedVersion,
                runningProcessObserved,
                selectedVersion,
                null,
                releases.Length,
                staged ? 1 : 0,
                0,
                releases,
                RunningExecutableSha256: runningSha256 ?? new string('b', 64)));
    }
}
