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
