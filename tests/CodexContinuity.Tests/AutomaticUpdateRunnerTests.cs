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

        await AutomaticUpdateRunner.RunAsync(
            root,
            "0.2.0",
            (_, _, _) =>
            {
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
                delays.Add(delay);
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(2, checks);
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
