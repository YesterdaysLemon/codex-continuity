using CodexContinuity.Tray;
using System.Diagnostics;
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

    [Fact]
    public void ParsesObservedStagedAndAppliedUpdateCounts()
    {
        const string json =
            """
            {
              "runningVersion": "0.2.1",
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
            new ContinuityUpdateSnapshot("0.2.1", "0.3.0", 2, 1, 0, "staged", null),
            update);
    }

    [Fact]
    public void UpdatePresentationLabelsAppliedHistoryAndKeepsVersionsOnFailure()
    {
        var update = new ContinuityUpdateSnapshot(
            "0.2.1",
            "0.3.0",
            2,
            1,
            1,
            "failed",
            "checksum mismatch");

        Assert.Equal(
            "Updates: 2 observed / 1 staged / 1 applied",
            TrayStatusPresentation.UpdateCounts(update));
        Assert.Equal(
            "Running v0.2.1; latest v0.3.0; last check failed: checksum mismatch",
            TrayStatusPresentation.UpdateDetail(update));
    }

    [Fact]
    public void UpdatePresentationExplainsRollbackAndFailedManualCheck()
    {
        var update = new ContinuityUpdateSnapshot(
            "0.2.1",
            "0.3.0",
            1,
            1,
            0,
            "deferred",
            null);

        var detail = TrayStatusPresentation.UpdateDetail(update);

        Assert.Equal("v0.3.0 deferred by rollback; running v0.2.1", detail);
        Assert.Equal(
            $"Manual update check failed; {detail}",
            TrayStatusPresentation.ManualCheckResult(succeeded: false, detail));
    }

    [Fact]
    public void UpdatePresentationDoesNotCallAStoppedSupervisorActive()
    {
        var update = new ContinuityUpdateSnapshot(
            "0.3.0",
            "0.3.0",
            1,
            1,
            1,
            "inactive",
            null);

        Assert.Equal(
            "Last ran v0.3.0; latest v0.3.0 is not active",
            TrayStatusPresentation.UpdateDetail(update));
    }

    [Fact]
    public void StatusAndDiagnosticsResolutionUseOwningPaths()
    {
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
            Assert.Equal(
                stateDirectory,
                TrayStatusClient.ResolveDiagnosticsDirectory(stateDirectory, legacyDirectory));

            File.Delete(installStatePath);
            Assert.Equal(
                legacyDirectory,
                TrayStatusClient.ResolveDiagnosticsDirectory(stateDirectory, legacyDirectory));
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
                $"Set-Content -LiteralPath '{recordPath}' -Value $PID; Start-Sleep -Seconds 15",
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
}
