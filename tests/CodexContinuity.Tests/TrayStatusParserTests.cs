using CodexContinuity.Tray;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class TrayStatusParserTests
{
    [Theory]
    [InlineData("{\"port\":45124}", 45124)]
    [InlineData("{}", TrayStatusClient.DefaultPort)]
    [InlineData("[]", TrayStatusClient.DefaultPort)]
    [InlineData("{\"port\":\"45124\"}", TrayStatusClient.DefaultPort)]
    [InlineData("{\"port\":0}", TrayStatusClient.DefaultPort)]
    [InlineData("{\"port\":-1}", TrayStatusClient.DefaultPort)]
    [InlineData("{\"port\":65536}", TrayStatusClient.DefaultPort)]
    [InlineData("{\"port\":2147483648}", TrayStatusClient.DefaultPort)]
    public void InstalledPortFallsBackForMissingOrInvalidValues(string json, int expected)
    {
        Assert.Equal(expected, TrayStatusClient.ParseInstalledPort(json));
    }

    [Fact]
    public void ParsesHealthyStatusWithoutReadingThreadNames()
    {
        const string json =
            """
            {
              "ready": true,
              "activeThreadCount": 3,
              "activeThreads": [{ "id": "secret", "name": "do not expose" }],
              "supervisor": { "state": "running" }
            }
            """;

        var status = TrayStatusParser.Parse(json);

        Assert.Equal(
            new TrayStatusSnapshot(ContinuityHealth.Healthy, 3, "Backend ready"),
            status);
        Assert.DoesNotContain("do not expose", status.Detail);
    }

    [Fact]
    public void TreatsReadyBackendWithUnknownSupervisorAsDegraded()
    {
        const string json = """{"ready":true,"activeThreadCount":1,"supervisor":null}""";

        var status = TrayStatusParser.Parse(json);

        Assert.Equal(ContinuityHealth.Degraded, status.Health);
    }

    [Fact]
    public void TreatsPreviouslyAttachedForeignBackendAsDegraded()
    {
        const string json =
            """{"ready":true,"activeThreadCount":1,"supervisor":{"state":"attached"}}""";

        var status = TrayStatusParser.Parse(json);

        Assert.Equal(ContinuityHealth.Degraded, status.Health);
    }

    [Theory, InlineData(0, false), InlineData(1, false), InlineData(2, true)]
    public void ShowsRecoveryOnlyWhileUnavailable(int health, bool expected) =>
        Assert.Equal(expected, TrayStatusPresentation.ShowRecovery((ContinuityHealth)health));

    [Fact]
    public void ParsesObservedStagedAndAppliedUpdateCounts()
    {
        const string json =
            """
            {
              "runningVersion": "0.2.1",
              "runningProcessObserved": true,
              "latestVersion": "0.3.0",
              "observedCount": 2,
              "stagedCount": 1,
              "appliedCount": 0,
              "latestState": "staged",
              "lastError": null
            }
            """;

        var update = TrayStatusParser.ParseUpdate(json);

        Assert.Equal(
            new ContinuityUpdateSnapshot("0.2.1", true, "0.3.0", 2, 1, 0, "staged", null),
            update);
        Assert.Equal(
            "Updates: 2 observed / 1 staged / 0 applied",
            TrayStatusPresentation.UpdateCounts(update));
    }

    [Theory]
    [MemberData(nameof(UpdateDetails))]
    public void PresentsEveryUpdateState(string state, bool running, bool live, string? error, string expected)
    {
        var update = new ContinuityUpdateSnapshot(
            "0.2.1", running, "0.3.0", 2, 1, 1, state, error);

        Assert.Equal(expected, TrayStatusPresentation.UpdateDetail(
            update, live ? ContinuityHealth.Healthy : ContinuityHealth.Unavailable));
    }

    public static TheoryData<string, bool, bool, string?, string> UpdateDetails => new()
    {
        { "active", true, true, null, "Running v0.2.1; latest is active" },
        { "staged", true, true, null, "Running v0.2.1; v0.3.0 staged" },
        { "staged", false, true, null, "Last ran v0.2.1; v0.3.0 staged" },
        { "deferred", true, true, null, "Running v0.2.1; v0.3.0 deferred by rollback" },
        { "inactive", false, true, null, "Last ran v0.2.1; latest v0.3.0 is not active" },
        { "ahead", true, true, null, "Running v0.2.1; ahead of stable v0.3.0" },
        { "failed", true, true, null, "Running v0.2.1; v0.3.0 could not be staged" },
        { "observed", true, true, null, "Running v0.2.1; v0.3.0 observed; staging pending" },
        { "unknown", true, true, null, "Running v0.2.1; update state unknown" },
        { "future", true, true, null, "Running v0.2.1; update state future" },
        { "failed", true, false, "checksum mismatch", "Last ran v0.2.1; latest v0.3.0; last check failed: checksum mismatch" },
    };

    [Fact]
    public void CompactsMultilineUpdateErrors() => Assert.Equal(
        $"Last ran v0.2.1; latest v0.3.0; last check failed: first  {new string('x', 153)}…",
        TrayStatusPresentation.UpdateDetail(new(
            "0.2.1", true, "0.3.0", 0, 0, 0, "failed", "first\r\n" + new string('x', 170)),
            ContinuityHealth.Unavailable));

    [Theory]
    [InlineData("stderr", "stdout", 2, "Update check failed: stderr")]
    [InlineData("", "stdout", 2, "Update check failed: stdout")]
    [InlineData("", "", 2, "Update check failed: exit code 2")]
    public void CommandFailureUsesBoundedUsefulDetail(
        string error, string output, int exitCode, string expected) => Assert.Equal(
            expected,
            TrayStatusPresentation.CommandFailure(
                "Update check", new TrayCommandResult(exitCode, output, error)));

    [Fact]
    public async Task MutationPresenterContainsLauncherFailureAndRestoresActions()
    {
        var enabledStates = new List<bool>();
        var feedback = string.Empty;
        await new TrayMutationPresenter().RunAsync(
            "Checkingâ€¦",
            "Update check",
            _ => throw new Win32Exception("launch failed"),
            CancellationToken.None,
            enabledStates.Add,
            text => feedback = text,
            () => throw new InvalidOperationException("Refresh must not run."));
        Assert.Equal([false, true], enabledStates);
        Assert.Equal("Update check failed: launch failed", feedback);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"observedCount\":\"many\"}")]
    public void InvalidUpdateShapeIsUnavailable(string json) => Assert.Equal(
        ContinuityUpdateSnapshot.Unavailable("Update status is invalid."),
        TrayStatusParser.ParseUpdate(json));

    [Fact]
    public void StatusAndDiagnosticsResolutionUseOwningPaths()
    {
        static string SupervisorStatus(int processId, DateTimeOffset updatedAt) =>
            $$"""{"supervisorProcessId":{{processId}},"updatedAtUtc":"{{updatedAt:O}}"}""";
        var root = Path.Combine(Path.GetTempPath(), $"continuity-tray-routing-{Guid.NewGuid():N}");
        var applicationDirectory = Path.Combine(root, "tray");
        var stateDirectory = Path.Combine(root, "state");
        var stableExecutable = Path.Combine(stateDirectory, "bin", "CodexContinuity.exe");
        var installStatePath = Path.Combine(stateDirectory, "install-state.json");
        var legacyDirectory = Path.Combine(root, "legacy");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stableExecutable)!);
            File.WriteAllText(stableExecutable, "fixture");
            File.WriteAllText(installStatePath, "{}");

            Assert.Equal(
                stableExecutable,
                TrayStatusClient.ResolveSupervisorExecutable(applicationDirectory, stateDirectory));
            Directory.CreateDirectory(legacyDirectory);
            File.WriteAllText(Path.Combine(legacyDirectory, "update-status.json"), "{}");
            var statusPath = Path.Combine(legacyDirectory, "supervisor-status.json");
            using var currentProcess = Process.GetCurrentProcess();
            File.WriteAllText(statusPath, SupervisorStatus(Environment.ProcessId,
                new DateTimeOffset(currentProcess.StartTime).AddMinutes(-1)));
            Assert.Equal(stateDirectory, TrayStatusClient.ResolveDiagnosticsDirectory(stateDirectory, legacyDirectory));
            File.WriteAllText(statusPath, SupervisorStatus(int.MaxValue, DateTimeOffset.UtcNow));
            Assert.Equal(stateDirectory, TrayStatusClient.ResolveDiagnosticsDirectory(stateDirectory, legacyDirectory));
            File.WriteAllText(statusPath, SupervisorStatus(Environment.ProcessId, DateTimeOffset.UtcNow));
            File.WriteAllText(Path.Combine(stateDirectory, "supervisor-status.json"), "{}");
            Assert.Equal(legacyDirectory, TrayStatusClient.ResolveDiagnosticsDirectory(stateDirectory, legacyDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void MutationResolutionUsesVersionedCommandsAndFailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"continuity-tray-mutation-{Guid.NewGuid():N}");
        var applicationDirectory = Path.Combine(root, "tray");
        var stateDirectory = Path.Combine(root, "state");
        var legacyDirectory = Path.Combine(root, "legacy");
        var bundledExecutable = Path.Combine(applicationDirectory, "CodexContinuity.exe");
        var stableExecutable = Path.Combine(stateDirectory, "bin", "CodexContinuity.exe");
        var installedExecutable = Path.Combine(stateDirectory, "versions", "v1", "CodexContinuity.exe");
        var statePath = Path.Combine(stateDirectory, "install-state.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stableExecutable)!);
            Directory.CreateDirectory(Path.GetDirectoryName(installedExecutable)!);
            Directory.CreateDirectory(applicationDirectory);
            File.WriteAllText(bundledExecutable, "bundled");
            File.WriteAllText(stableExecutable, "stable");
            File.WriteAllText(installedExecutable, "versioned");
            WriteInstallState(statePath, installedExecutable, "AA", lifecycle: 0);

            Assert.Equal(
                new TrayMutationTarget(
                    Path.GetFullPath(installedExecutable),
                    Path.GetFullPath(installedExecutable),
                    "AA",
                    null),
                TrayStatusClient.ResolveMutationTarget(
                    applicationDirectory, stateDirectory, legacyDirectory));

            File.Delete(installedExecutable);
            Assert.Equal(
                new TrayMutationTarget(
                    Path.GetFullPath(bundledExecutable),
                    Path.GetFullPath(installedExecutable),
                    "AA",
                    null),
                TrayStatusClient.ResolveMutationTarget(
                    applicationDirectory, stateDirectory, legacyDirectory));

            WriteInstallState(statePath, stableExecutable, "BB", lifecycle: 0);
            Assert.Equal(
                new TrayMutationTarget(
                    Path.GetFullPath(bundledExecutable),
                    Path.GetFullPath(stableExecutable),
                    "BB",
                    null),
                TrayStatusClient.ResolveMutationTarget(
                    applicationDirectory, stateDirectory, legacyDirectory));

            WriteInstallState(statePath, stableExecutable, "BB", lifecycle: 1);
            var deferred = TrayStatusClient.ResolveMutationTarget(
                applicationDirectory, stateDirectory, legacyDirectory);
            Assert.False(deferred.Available);
            Assert.Contains("deferred uninstall", deferred.Error);

            foreach (var invalidState in new[] { "not json", """{"lifecycle":9}""" })
            {
                File.WriteAllText(statePath, invalidState);
                var invalid = TrayStatusClient.ResolveMutationTarget(
                    applicationDirectory, stateDirectory, legacyDirectory);
                Assert.False(invalid.Available);
                Assert.Contains("state is invalid", invalid.Error);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MutationActionsUseOneGateAndExactVersionedCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), $"continuity-tray-actions-{Guid.NewGuid():N}");
        var applicationDirectory = Path.Combine(root, "tray");
        var stateDirectory = Path.Combine(root, "state");
        var legacyDirectory = Path.Combine(root, "legacy");
        var installedExecutable = Path.Combine(stateDirectory, "versions", "v1", "CodexContinuity.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(installedExecutable)!);
        Directory.CreateDirectory(applicationDirectory);
        File.WriteAllText(installedExecutable, "versioned");
        WriteInstallState(
            Path.Combine(stateDirectory, "install-state.json"),
            installedExecutable,
            "ABC123",
            lifecycle: 0);
        var calls = new List<string>();
        var firstEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<TrayCommandResult> RunProcess(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken _)
        {
            calls.Add($"{executable}|{string.Join('|', arguments)}");
            if (arguments[0] == "update")
            {
                firstEntered.SetResult(true);
                await releaseFirst.Task;
            }
            else
            {
                secondEntered.SetResult(true);
            }
            return new(0, string.Empty, string.Empty);
        }

        try
        {
            var client = new TrayStatusClient(
                "read-only-status.exe",
                applicationDirectory,
                stateDirectory,
                legacyDirectory,
                RunProcess);
            var update = client.CheckForUpdatesAsync(CancellationToken.None);
            await firstEntered.Task;
            var recovery = client.RestartSupervisorAsync(CancellationToken.None);

            Assert.False(secondEntered.Task.IsCompleted);
            releaseFirst.SetResult(true);
            await Task.WhenAll(update, recovery);
            Assert.Equal(
                [
                    $"{Path.GetFullPath(installedExecutable)}|update",
                    $"{Path.GetFullPath(installedExecutable)}|repair|--start-now|" +
                        $"--expected-installed-executable|{Path.GetFullPath(installedExecutable)}|" +
                        "--expected-installed-sha256|ABC123",
                ],
                calls);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MutationGateReleasesAfterFaultAndSkipsCanceledQueue()
    {
        var gate = new TrayCommandGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RunAsync<int>(
            () => throw new InvalidOperationException("fixture"),
            CancellationToken.None));
        Assert.Equal(1, await gate.RunAsync(() => Task.FromResult(1), CancellationToken.None));

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = gate.RunAsync(async () =>
        {
            await release.Task;
            return 1;
        }, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var entered = false;
        var queued = gate.RunAsync(() =>
        {
            entered = true;
            return Task.FromResult(2);
        }, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(entered);
        release.SetResult(true);
        Assert.Equal(1, await held);
    }

    [Fact]
    public async Task CancelingTrayProcessKillsTheChildProcessTree()
    {
        var recordPath = Path.GetTempFileName();
        File.Delete(recordPath);
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var commandTask = TrayStatusClient.RunProcessAsync(
            powershell,
            [
                "-NoProfile",
                "-Command",
                $"$child = Start-Process -FilePath '{powershell}' -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 15' -WindowStyle Hidden -PassThru; Set-Content -LiteralPath '{recordPath}' -Value $child.Id; Wait-Process -Id $child.Id",
            ],
            cancellation.Token);
        try
        {
            while (!File.Exists(recordPath) && !commandTask.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25));
            }
            var processId = int.Parse(await File.ReadAllTextAsync(recordPath));

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commandTask);

            Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
        }
        finally
        {
            File.Delete(recordPath);
        }
    }

    private static void WriteInstallState(
        string path,
        string installedExecutable,
        string binarySha256,
        int lifecycle) => File.WriteAllText(
            path,
            JsonSerializer.Serialize(new { installedExecutable, binarySha256, lifecycle }));
}
