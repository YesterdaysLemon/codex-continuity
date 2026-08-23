using CodexContinuity;
using CodexContinuity.ProcessHarness;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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

    [Theory]
    [InlineData(null)]
    [InlineData("stopped")]
    [InlineData("foreignEndpoint")]
    public void ExactExpectedExecutableBlocksWithMissingOrTerminalStatus(string? state)
    {
        Directory.CreateDirectory(root);
        var executable = CopyHarnessAsLegacyExecutable("expected");
        var process = StartIdleProcess(executable, "expected");
        if (state is not null)
        {
            new SupervisorStatusStore(ContinuityPaths.SupervisorStatusFile(root)).Write(
                Status(process) with { State = state });
        }
        var scope = new SupervisorCompatibilityScope([root])
        {
            ExpectedExecutables = [executable],
        };

        Assert.Throws<InvalidOperationException>(() =>
            SupervisorCompatibilityGuard.EnsureInactive(
                scope,
                "test expected executable"));

        Assert.False(process.HasExited);
    }

    [Fact]
    public void SameNamedProcessAtAnotherPathDoesNotBlockExactDiscovery()
    {
        Directory.CreateDirectory(root);
        var runningExecutable = CopyHarnessAsLegacyExecutable("running");
        var expectedExecutable = CopyHarnessAsLegacyExecutable("expected-other");
        var process = StartIdleProcess(runningExecutable, "same-name");

        SupervisorCompatibilityGuard.EnsureInactive(
            new SupervisorCompatibilityScope([root])
            {
                ExpectedExecutables = [expectedExecutable],
            },
            "test exact path");

        Assert.False(process.HasExited);
    }

    [Fact]
    public void InaccessibleSameNameProcessFailsClosedButCurrentProcessIsIgnored()
    {
        var executable = Path.Combine(root, "CodexContinuity.exe");
        var scope = new SupervisorCompatibilityScope([root])
        {
            ExpectedExecutables = [executable],
        };

        Assert.Equal(
            ExpectedSupervisorProcessState.Unsafe,
            SupervisorCompatibilityGuard.InspectExpectedExecutables(
                [executable],
                processName =>
                {
                    Assert.Equal("CodexContinuity", processName);
                    return [new SupervisorProcessSnapshot(int.MaxValue, ExecutablePath: null)];
                }));
        Assert.Throws<InvalidOperationException>(() =>
            SupervisorCompatibilityGuard.EnsureInactive(
                scope,
                "test inaccessible process",
                _ => ExpectedSupervisorProcessState.Unsafe));
        Assert.Equal(
            ExpectedSupervisorProcessState.Missing,
            SupervisorCompatibilityGuard.InspectExpectedExecutables(
                [executable],
                _ => [new SupervisorProcessSnapshot(
                    Environment.ProcessId,
                    ExecutablePath: null)]));
    }

    [Fact]
    public void ExpectedExecutableInspectionScansEachNameOnceAndChecksEverySnapshot()
    {
        var first = Path.Combine(root, "first", "CodexContinuity.exe");
        var second = Path.Combine(root, "second", "CodexContinuity.exe");
        var wrong = Path.Combine(root, "other", "CodexContinuity.exe");
        var snapshotCalls = 0;

        ExpectedSupervisorProcessState Inspect(params SupervisorProcessSnapshot[] snapshots)
        {
            snapshotCalls = 0;
            var state = SupervisorCompatibilityGuard.InspectExpectedExecutables(
                [first, second],
                _ =>
                {
                    snapshotCalls++;
                    return snapshots;
                });
            Assert.Equal(1, snapshotCalls);
            return state;
        }

        Assert.Equal(
            ExpectedSupervisorProcessState.Active,
            Inspect(new(int.MaxValue - 1, wrong), new(int.MaxValue, second.ToUpperInvariant())));
        Assert.Equal(
            ExpectedSupervisorProcessState.Unsafe,
            Inspect(new(int.MaxValue - 1, wrong), new(int.MaxValue, ExecutablePath: null)));
    }

    [Theory]
    [InlineData("exactProcess")]
    [InlineData("activeRecord")]
    [InlineData("unsafeRecord")]
    public async Task CombinedGuardBlocksServeAndDifferentPortMutation(string evidence)
    {
        Directory.CreateDirectory(root);
        var legacyRoot = Path.Combine(root, "legacy");
        Process? process = null;
        if (evidence == "exactProcess")
        {
            var executable = CopyHarnessAsLegacyExecutable(
                Path.Combine("versions", "integration"));
            process = StartIdleProcess(executable, "integration");
        }
        else if (evidence == "activeRecord")
        {
            process = StartIdleProcess(HarnessExecutable(), "integration");
            new SupervisorStatusStore(ContinuityPaths.SupervisorStatusFile(legacyRoot))
                .Write(Status(process));
        }
        else
        {
            Directory.CreateDirectory(legacyRoot);
            File.WriteAllText(
                ContinuityPaths.SupervisorStatusFile(legacyRoot),
                "{not-json");
        }
        var stateDirectories = new[] { root, legacyRoot };
        var coordinator = new InstallCoordinator(
            root,
            new InstallPortSafetyTests.StartupOnlyInstallPlatform(),
            new InstallStateStore(ContinuityPaths.InstallStateFile(root)),
            legacyRoot);
        var installedPort = FindAvailablePort();
        var requestedPort = FindAvailablePort(installedPort);
        var mutationRan = false;
        var updaterRan = false;
        string? reportedFailure = null;

        static string ExpectedFailure(string evidence, string operation) => evidence switch
        {
            "exactProcess" =>
                $"A previous Continuity supervisor executable is still active. Refusing to {operation}.",
            "activeRecord" =>
                $"A recorded Continuity supervisor is still active. Refusing to {operation}.",
            "unsafeRecord" =>
                $"Persisted supervisor identity cannot be trusted. Refusing to {operation}.",
            _ => throw new ArgumentOutOfRangeException(nameof(evidence)),
        };

        Assert.Equal(1, await Program.ServeAsync(
            FindAvailablePort(installedPort, requestedPort),
            root,
            stateDirectories,
            coordinator,
            (_, _, _) =>
            {
                updaterRan = true;
                return Task.CompletedTask;
            },
            failure =>
            {
                reportedFailure = failure;
                return 1;
            }));
        Assert.Equal(ExpectedFailure(evidence, "start another supervisor"), reportedFailure);
        Assert.False(updaterRan);
        var mutationException = Assert.Throws<InvalidOperationException>(() =>
            Program.RunInstallMutation(
            root,
            stateDirectories,
            coordinator,
            installedPort,
            requestedPort,
            () => mutationRan = true));
        Assert.Equal(
            ExpectedFailure(evidence, "change the configured port"),
            mutationException.Message);

        Assert.False(mutationRan);
        Assert.Equal("staged", Program.RunInstallMutation(
            root,
            stateDirectories,
            coordinator,
            installedPort,
            installedPort,
            () => "staged"));
        Assert.True(process is null || !process.HasExited);
    }

    [Fact]
    public void SamePortProductionMutationDoesNotBuildCompatibilityScope()
    {
        var legacyRoot = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(ContinuityPaths.InstallStateFile(legacyRoot), "{not-json");
        var coordinator = new InstallCoordinator(
            root,
            new InstallPortSafetyTests.StartupOnlyInstallPlatform(),
            new InstallStateStore(ContinuityPaths.InstallStateFile(root)),
            legacyRoot);

        Assert.Equal("staged", Program.RunInstallMutation(
            root,
            [root, legacyRoot],
            coordinator,
            installedPort: 45123,
            requestedPort: 45123,
            () => "staged"));
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

    private static int FindAvailablePort(params int[] excludedPorts)
    {
        while (true)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (!excludedPorts.Contains(port))
            {
                return port;
            }
        }
    }

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
