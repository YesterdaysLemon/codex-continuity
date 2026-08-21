using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class AutomaticUpdateRunnerTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-runner-tests-{Guid.NewGuid():N}");

    [Fact]
    public void MissingSelectedExecutableIsTreatedAsUnselected()
    {
        Assert.Null(AutomaticUpdateRunner.ResolveSelectedVersion(Path.Combine(root, "missing.exe")));
    }

    [Fact]
    public async Task PollsImmediatelyAndContinuesAfterCheckFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var checks = 0;
        var delays = new List<TimeSpan>();
        var events = new List<string>();

        await AutomaticUpdateRunner.RunAsync(
            root,
            "0.2.0",
            (_, _, _) =>
            {
                events.Add("check");
                checks++;
                if (checks == 1)
                {
                    throw new InvalidOperationException("transient failure");
                }
                cancellation.Cancel();
                return Task.CompletedTask;
            },
            (delay, token) =>
            {
                events.Add("delay");
                delays.Add(delay);
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(2, checks);
        Assert.Equal(["check", "delay", "check", "delay"], events);
        Assert.Equal(
            [AutomaticUpdateRunner.CheckInterval, AutomaticUpdateRunner.CheckInterval],
            delays);
    }

    [Fact]
    public async Task CheckOnceUsesInstalledPortAndRecordsLiveRunningVersion()
    {
        const int installedPort = 45999;
        Directory.CreateDirectory(root);
        new InstallStateStore(ContinuityPaths.InstallStateFile(root)).Save(
            InstallState(installedPort, Environment.ProcessPath!));
        int? stagedPort = null;
        TrayInstallMode? stagedTrayMode = null;

        var state = await AutomaticUpdateRunner.CheckOnceAsync(
            root,
            "0.2.0",
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(
                [Release("0.3.0")]),
            (_, port, trayMode) =>
            {
                stagedPort = port;
                stagedTrayMode = trayMode;
                return Task.CompletedTask;
            },
            () => DateTimeOffset.Parse("2026-08-21T13:00:00Z"),
            CancellationToken.None);

        Assert.NotNull(state);
        Assert.Equal(installedPort, stagedPort);
        Assert.Equal(TrayInstallMode.Disabled, stagedTrayMode);
        Assert.True(state.RunningProcessObserved);
        Assert.Equal("staged", state.LatestState);
        Assert.True(File.Exists(ContinuityPaths.UpdateLockFile(root)));
    }

    [Fact]
    public async Task CheckOnceTreatsPersistedVersionAsInactiveWithoutLiveSupervisor()
    {
        var now = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        Directory.CreateDirectory(root);
        new InstallStateStore(ContinuityPaths.InstallStateFile(root)).Save(
            InstallState(45999, Path.Combine(root, "missing.exe")));
        new ContinuityUpdateStateStore(ContinuityPaths.UpdateStatusFile(root)).Save(
            new ContinuityUpdateState(
                SchemaVersion: 1,
                TrackingStartedAtUtc: now,
                LastCheckedAtUtc: now,
                BaselineVersion: "0.2.0",
                RunningVersion: "0.3.0",
                SelectedVersion: "0.3.0",
                RunningProcessObserved: true,
                LatestVersion: "0.3.0",
                LastError: null,
                ObservedCount: 1,
                StagedCount: 1,
                AppliedCount: 1,
                Releases: [new TrackedContinuityRelease(
                    "0.3.0",
                    now,
                    now,
                    now,
                    now,
                    LastError: null)]));

        var state = await AutomaticUpdateRunner.CheckOnceAsync(
            root,
            runningVersion: null,
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(
                [Release("0.3.0")]),
            (_, _, _) => throw new InvalidOperationException("The current release must not stage."),
            () => now.AddMinutes(1),
            CancellationToken.None);

        Assert.NotNull(state);
        Assert.False(state.RunningProcessObserved);
        Assert.Equal(1, state.AppliedCount);
        Assert.Equal("inactive", state.LatestState);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static InstallState InstallState(int port, string executable) => new(
        SchemaVersion: 4,
        Port: port,
        InstalledExecutable: executable,
        PreviousInstalledExecutable: null,
        InstalledTrayExecutable: null,
        PreviousInstalledTrayExecutable: null,
        BinarySha256: "fixture",
        AppServerUrl: new OwnedString(null, LoopbackEndpoint.WebSocketUrl(port)),
        UpdaterSetting: new OwnedString(null, "false"),
        CommandPath: null,
        StartupCommand: new OwnedString(null, "fixture"),
        TrayStartupCommand: null,
        PreviousInstalledAppRegistration: null,
        InstalledAppRegistration: null,
        InstalledAtUtc: DateTimeOffset.Parse("2026-08-21T12:00:00Z"));

    private static PublishedContinuityRelease Release(string version) => new(
        version,
        DateTimeOffset.Parse("2026-08-21T12:00:00Z"),
        $"https://example.test/v{version}/archive",
        $"https://example.test/v{version}/checksum");
}
