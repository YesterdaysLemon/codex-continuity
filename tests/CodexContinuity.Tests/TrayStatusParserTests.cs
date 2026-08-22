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
        Assert.Equal(
            "Updates: 2 observed / 1 staged / 0 applied",
            TrayStatusPresentation.UpdateCounts(update));
    }

    [Theory]
    [MemberData(nameof(UpdateDetails))]
    public void PresentsEveryUpdateState(string state, string? error, string expected)
    {
        var update = new ContinuityUpdateSnapshot(
            "0.2.1",
            "0.3.0",
            2,
            1,
            1,
            state,
            error);

        Assert.Equal(expected, TrayStatusPresentation.UpdateDetail(update));
    }

    public static TheoryData<string, string?, string> UpdateDetails => new()
    {
        { "active", null, "Running v0.2.1; latest is active" },
        { "staged", null, "v0.3.0 staged; running v0.2.1" },
        { "deferred", null, "v0.3.0 deferred by rollback; running v0.2.1" },
        { "inactive", null, "Last ran v0.2.1; latest v0.3.0 is not active" },
        { "ahead", null, "Running v0.2.1; ahead of stable v0.3.0" },
        { "failed", null, "v0.3.0 could not be staged; running v0.2.1" },
        { "observed", null, "v0.3.0 observed; staging pending" },
        { "unknown", null, "Running v0.2.1; update state unknown" },
        { "future", null, "Running v0.2.1; update state future" },
        { "failed", "checksum mismatch", "Running v0.2.1; latest v0.3.0; last check failed: checksum mismatch" },
    };

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"observedCount\":\"many\"}")]
    public void InvalidUpdateShapeIsUnavailable(string json) => Assert.Equal(
        ContinuityUpdateSnapshot.Unavailable("Update status is invalid."),
        TrayStatusParser.ParseUpdate(json));

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
}
