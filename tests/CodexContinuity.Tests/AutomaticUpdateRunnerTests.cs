using CodexContinuity;
using System.Security.Cryptography;
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
        Assert.Null(AutomaticUpdateRunner.ResolveBuildIdentity(Path.Combine(root, "missing.exe")));
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
    public async Task PollingContinuesAfterANonShutdownCancellation()
    {
        using var shutdown = new CancellationTokenSource();
        var checks = 0;

        await AutomaticUpdateRunner.RunAsync(
            root,
            "0.2.0",
            (_, _, _) =>
            {
                checks++;
                if (checks == 1)
                {
                    throw new OperationCanceledException("check timeout");
                }
                shutdown.Cancel();
                return Task.CompletedTask;
            },
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            shutdown.Token);

        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task CheckOnceDistinguishesBusyMissingAndDeferredLifecycle()
    {
        var missing = await AutomaticUpdateRunner.CheckOnceAsync(
            root,
            "0.2.0",
            CancellationToken.None);
        Assert.Equal(AutomaticUpdateCheckKind.NotInstalled, missing.Kind);

        Directory.CreateDirectory(root);
        var stateStore = new InstallStateStore(ContinuityPaths.InstallStateFile(root));
        stateStore.Save(InstallState(45999, Environment.ProcessPath!) with
        {
            Lifecycle = InstallLifecycle.DeferredUninstall,
        });
        var deferred = await AutomaticUpdateRunner.CheckOnceAsync(
            root,
            "0.2.0",
            CancellationToken.None);
        Assert.Equal(AutomaticUpdateCheckKind.DeferredUninstall, deferred.Kind);

        stateStore.Save(InstallState(45999, Environment.ProcessPath!));
        await using var updateLock = new FileStream(
            ContinuityPaths.UpdateLockFile(root),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var busy = await AutomaticUpdateRunner.CheckOnceAsync(
            root,
            "0.2.0",
            _ => throw new InvalidOperationException("A busy check must not use the network."),
            (_, _, _, _) => throw new InvalidOperationException("A busy check must not stage."),
            () => DateTimeOffset.Parse("2026-08-21T13:00:00Z"),
            CancellationToken.None);
        Assert.Equal(AutomaticUpdateCheckKind.Busy, busy.Kind);
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

        var result = await AutomaticUpdateRunner.CheckOnceAsync(
            root,
            "0.2.0",
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(
                [Release("0.3.0")]),
            (_, installState, trayMode, _) =>
            {
                stagedPort = installState.Port;
                stagedTrayMode = trayMode;
                return Task.FromResult(StagedBuild());
            },
            () => DateTimeOffset.Parse("2026-08-21T13:00:00Z"),
            CancellationToken.None);

        var state = result.State;
        Assert.Equal(AutomaticUpdateCheckKind.Completed, result.Kind);
        Assert.NotNull(state);
        Assert.Equal(installedPort, stagedPort);
        Assert.Equal(TrayInstallMode.Disabled, stagedTrayMode);
        Assert.True(state.RunningProcessObserved);
        Assert.Equal("staged", state.LatestState);
        Assert.True(File.Exists(ContinuityPaths.UpdateLockFile(root)));
    }

    [Fact]
    public async Task CheckOnceRepairsMissingSelectionWithoutClaimingALiveSupervisor()
    {
        var now = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        Directory.CreateDirectory(root);
        new InstallStateStore(ContinuityPaths.InstallStateFile(root)).Save(
            InstallState(45999, Path.Combine(root, "missing.exe")));
        new SupervisorStatusStore(ContinuityPaths.SupervisorStatusFile(root)).Write(
            new SupervisorStatus(
                State: "stopped",
                SupervisorProcessId: int.MaxValue,
                BackendProcessId: null,
                Port: 45999,
                CodexHome: null,
                ConsecutiveFailures: 0,
                LastExitCode: null,
                UpdatedAtUtc: now,
                NextRetryAtUtc: null,
                Detail: "test fixture"));
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

        var staged = 0;
        var result = await AutomaticUpdateRunner.CheckOnceAsync(
            root,
            runningVersion: null,
            _ => Task.FromResult<IReadOnlyList<PublishedContinuityRelease>>(
                [Release("0.3.0")]),
            (_, _, _, _) =>
            {
                staged++;
                return Task.FromResult(StagedBuild());
            },
            () => now.AddMinutes(1),
            CancellationToken.None);

        var state = result.State;
        Assert.Equal(AutomaticUpdateCheckKind.Completed, result.Kind);
        Assert.NotNull(state);
        Assert.Equal(1, staged);
        Assert.False(state.RunningProcessObserved);
        Assert.Equal(1, state.AppliedCount);
        Assert.Equal("inactive", state.LatestState);
    }

    [Fact]
    public async Task StagedProofWaitsForTheLifecycleLock()
    {
        Directory.CreateDirectory(root);
        var previousExecutable = Path.Combine(root, "previous.exe");
        var stagedExecutable = Path.Combine(root, "staged.exe");
        await File.WriteAllTextAsync(previousExecutable, "previous");
        await File.WriteAllTextAsync(stagedExecutable, "staged");
        var previousSha256 = Sha256(previousExecutable);
        var stagedSha256 = Sha256(stagedExecutable);
        var previousState = InstallState(45999, previousExecutable) with
        {
            BinarySha256 = previousSha256,
        };
        new InstallStateStore(ContinuityPaths.InstallStateFile(root)).Save(
            InstallState(45999, stagedExecutable) with
            {
                PreviousInstalledExecutable = previousExecutable,
                BinarySha256 = stagedSha256,
            });

        var heldLock = ContinuityLifecycleLock.Acquire(root);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var proofTask = Task.Run(() =>
        {
            started.SetResult();
            return AutomaticUpdateRunner.VerifyStagedBuild(root, previousState);
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Assert.False(proofTask.IsCompleted);
        }
        finally
        {
            heldLock.Dispose();
        }

        Assert.Equal(
            new StagedContinuityBuild(stagedSha256, previousSha256),
            await proofTask.WaitAsync(TimeSpan.FromSeconds(5)));
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

    private static StagedContinuityBuild StagedBuild() =>
        new(new string('b', 64), new string('a', 64));

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
