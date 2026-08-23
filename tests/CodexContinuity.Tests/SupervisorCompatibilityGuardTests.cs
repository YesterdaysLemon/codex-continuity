using CodexContinuity;
using CodexContinuity.ProcessHarness;
using System.Diagnostics;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class SupervisorCompatibilityGuardTests : IDisposable
{
    private readonly List<Process> startedProcesses = [];
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-supervisor-compatibility-{Guid.NewGuid():N}");

    [Fact]
    public void MissingEveryStateDirectoryAllowsClassificationToComplete()
    {
        var legacyRoot = Path.Combine(root, "legacy");

        SupervisorCompatibilityGuard.EnsureNoActiveRecord(
            new SupervisorCompatibilityScope([root, legacyRoot]),
            "test missing evidence");

        Assert.False(Directory.Exists(root));
        Assert.False(Directory.Exists(legacyRoot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentIdentityInEveryStateDirectoryBlocksWithoutChangingEvidence(
        bool useLegacyDirectory)
    {
        Directory.CreateDirectory(root);
        var legacyRoot = Path.Combine(root, "legacy");
        var process = StartIdleProcess(HarnessExecutable(), "current");
        var statusDirectory = useLegacyDirectory ? legacyRoot : root;
        var statusPath = ContinuityPaths.SupervisorStatusFile(statusDirectory);
        new SupervisorStatusStore(statusPath).Write(Status(process));
        var statusBytes = File.ReadAllBytes(statusPath);
        var scope = new SupervisorCompatibilityScope([root, legacyRoot]);

        Assert.Throws<InvalidOperationException>(() =>
            SupervisorCompatibilityGuard.EnsureNoActiveRecord(
                scope,
                "test current identity"));

        Assert.Equal(statusBytes, File.ReadAllBytes(statusPath));
        Assert.False(process.HasExited);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UnsafeStatusInEveryStateDirectoryFailsClosed(
        bool useLegacyDirectory,
        bool lockStatus)
    {
        Directory.CreateDirectory(root);
        var legacyRoot = Path.Combine(root, "legacy");
        var statusDirectory = useLegacyDirectory ? legacyRoot : root;
        Directory.CreateDirectory(statusDirectory);
        var statusPath = ContinuityPaths.SupervisorStatusFile(statusDirectory);
        File.WriteAllText(statusPath, "{not-json");
        var statusBytes = File.ReadAllBytes(statusPath);
        var scope = new SupervisorCompatibilityScope([root, legacyRoot]);
        using (var locked = lockStatus
                   ? new FileStream(
                       statusPath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None)
                   : null)
        {
            Assert.Throws<InvalidOperationException>(() =>
                SupervisorCompatibilityGuard.EnsureNoActiveRecord(
                    scope,
                    "test unsafe evidence"));
        }

        Assert.Equal(statusBytes, File.ReadAllBytes(statusPath));
    }

    [Fact]
    public void CompleteLegacyIdentityBlocksWithoutKillingTheProcess()
    {
        Directory.CreateDirectory(root);
        var executable = CopyHarnessAsLegacyExecutable("legacy-status");
        var process = StartIdleProcess(executable, "legacy-status");
        Assert.Equal("CodexContinuity", process.ProcessName);
        var status = Status(process) with
        {
            SupervisorStartedAtUtc = null,
            SupervisorExecutable = null,
        };
        new SupervisorStatusStore(ContinuityPaths.SupervisorStatusFile(root)).Write(status);

        Assert.Throws<InvalidOperationException>(() =>
            SupervisorCompatibilityGuard.EnsureNoActiveRecord(
                SupervisorCompatibilityScope.ForStateDirectory(root),
                "test compatibility"));

        Assert.False(process.HasExited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusedProcessIdentityIsTreatedAsStale(bool mismatchStartTime)
    {
        Directory.CreateDirectory(root);
        var process = StartIdleProcess(HarnessExecutable(), "stale");
        var status = mismatchStartTime
            ? Status(process) with
            {
                SupervisorStartedAtUtc = process.StartTime.ToUniversalTime().AddSeconds(1),
            }
            : Status(process) with
            {
                SupervisorExecutable = Path.Combine(root, "another.exe"),
            };
        new SupervisorStatusStore(ContinuityPaths.SupervisorStatusFile(root)).Write(status);

        SupervisorCompatibilityGuard.EnsureNoActiveRecord(
            SupervisorCompatibilityScope.ForStateDirectory(root),
            "test stale evidence");

        Assert.False(process.HasExited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyEvidenceRequiresSafeNameAndTimestamp(bool wrongProcessName)
    {
        Directory.CreateDirectory(root);
        var executable = wrongProcessName
            ? HarnessExecutable()
            : CopyHarnessAsLegacyExecutable("legacy-stale");
        var process = StartIdleProcess(executable, "legacy-stale");
        var status = Status(process) with
        {
            UpdatedAtUtc = wrongProcessName
                ? DateTimeOffset.UtcNow
                : process.StartTime.ToUniversalTime().AddSeconds(-1),
            SupervisorStartedAtUtc = null,
            SupervisorExecutable = null,
        };
        new SupervisorStatusStore(ContinuityPaths.SupervisorStatusFile(root)).Write(status);

        Assert.Equal(
            wrongProcessName
                ? RecordedSupervisorState.Stale
                : RecordedSupervisorState.Unsafe,
            SupervisorCompatibilityGuard.Inspect(status));

        Assert.False(process.HasExited);
    }

    [Theory]
    [InlineData("stopped")]
    [InlineData("foreignEndpoint")]
    public void TerminalSupervisorStatusDoesNotClaimLiveOwnership(string state)
    {
        Directory.CreateDirectory(root);
        var process = StartIdleProcess(HarnessExecutable(), "terminal");
        new SupervisorStatusStore(ContinuityPaths.SupervisorStatusFile(root)).Write(
            Status(process) with { State = state });

        SupervisorCompatibilityGuard.EnsureNoActiveRecord(
            SupervisorCompatibilityScope.ForStateDirectory(root),
            "test terminal status");

        Assert.False(process.HasExited);
    }

    [Theory]
    [InlineData("stopped", false)]
    [InlineData("stopped", true)]
    [InlineData("foreignEndpoint", false)]
    [InlineData("foreignEndpoint", true)]
    public void TerminalRecordDoesNotHideLaterActiveOrUnsafeEvidence(
        string terminalState,
        bool unsafeLegacyEvidence)
    {
        Directory.CreateDirectory(root);
        var legacyRoot = Path.Combine(root, "legacy");
        var process = StartIdleProcess(HarnessExecutable(), "terminal-ordering");
        var currentPath = ContinuityPaths.SupervisorStatusFile(root);
        var legacyPath = ContinuityPaths.SupervisorStatusFile(legacyRoot);
        new SupervisorStatusStore(currentPath).Write(
            Status(process) with { State = terminalState });
        if (unsafeLegacyEvidence)
        {
            Directory.CreateDirectory(legacyRoot);
            File.WriteAllText(legacyPath, "{not-json");
        }
        else
        {
            new SupervisorStatusStore(legacyPath).Write(Status(process));
        }
        var currentBytes = File.ReadAllBytes(currentPath);
        var legacyBytes = File.ReadAllBytes(legacyPath);

        Assert.Throws<InvalidOperationException>(() =>
            SupervisorCompatibilityGuard.EnsureNoActiveRecord(
                new SupervisorCompatibilityScope([root, legacyRoot]),
                "test directory ordering"));

        Assert.Equal(currentBytes, File.ReadAllBytes(currentPath));
        Assert.Equal(legacyBytes, File.ReadAllBytes(legacyPath));
        Assert.False(process.HasExited);
    }

    private Process StartIdleProcess(string executable, string name)
    {
        var readyPath = Path.Combine(root, $"{name}-{Guid.NewGuid():N}.ready");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        startInfo.ArgumentList.Add("idle-process");
        startInfo.ArgumentList.Add(readyPath);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the process harness.");
        startedProcesses.Add(process);
        if (!SpinWait.SpinUntil(
                () => File.Exists(readyPath) || process.HasExited,
                TimeSpan.FromSeconds(5)) ||
            process.HasExited)
        {
            throw new InvalidOperationException("The process harness did not become ready.");
        }
        return process;
    }

    private string CopyHarnessAsLegacyExecutable(string name)
    {
        var sourceDirectory = Path.GetDirectoryName(typeof(HarnessMarker).Assembly.Location)!;
        var destinationDirectory = Path.Combine(root, name);
        Directory.CreateDirectory(destinationDirectory);
        foreach (var source in Directory.EnumerateFiles(sourceDirectory))
        {
            var filename = Path.GetFileName(source);
            if (filename.StartsWith(
                    "CodexContinuity.ProcessHarness.",
                    StringComparison.OrdinalIgnoreCase) ||
                filename.Equals("CodexContinuity.dll", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, Path.Combine(destinationDirectory, filename));
            }
        }
        var executable = Path.Combine(destinationDirectory, "CodexContinuity.exe");
        File.Copy(HarnessExecutable(), executable);
        return executable;
    }

    private static string HarnessExecutable() =>
        Path.ChangeExtension(typeof(HarnessMarker).Assembly.Location, ".exe");

    private static SupervisorStatus Status(Process process) => new(
        "running",
        process.Id,
        BackendProcessId: null,
        Port: 45123,
        CodexHome: null,
        ConsecutiveFailures: 0,
        LastExitCode: null,
        DateTimeOffset.UtcNow,
        NextRetryAtUtc: null,
        "test",
        process.StartTime.ToUniversalTime(),
        process.MainModule!.FileName);

    public void Dispose()
    {
        foreach (var process in startedProcesses)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit();
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
