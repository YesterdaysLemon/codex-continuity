using CodexContinuity.ProcessHarness;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class SupervisorRelayTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-supervisor-relay-{Guid.NewGuid():N}");
    private readonly ConcurrentQueue<HarnessIdentity> harnesses = new();

    [Fact]
    public async Task GatesPublicEndpointUntilPrivateBackendIsReady()
    {
        var publicPort = FindAvailablePort();
        var fixtureStartedPath = Path.Combine(root, "fixture-started.txt");
        var startGatePath = Path.Combine(root, "start-gate.txt");
        var privatePort = 0;
        var backendProcessId = 0;
        var backendStartedAtUtc = default(DateTimeOffset);
        var backendExecutable = string.Empty;
        using var shutdown = new CancellationTokenSource();

        WindowsProcessGroup StartBackend(int port)
        {
            privatePort = port;
            var process = StartHarnessBackend(port, fixtureStartedPath, startGatePath);
            backendProcessId = process.Id;
            backendStartedAtUtc = process.StartedAtUtc;
            backendExecutable = process.ExecutablePath;
            return process;
        }

        var supervisor = OwnedSupervisorRuntime.RunAsync(
            publicPort, root, shutdown.Token, StartBackend);
        try
        {
            await WaitForFileAsync(fixtureStartedPath);
            await AssertBackendNotReadyAsync(privatePort);
            await AssertEndpointUnavailableAsync(publicPort);
            Assert.False(CanBind(publicPort));

            await File.WriteAllTextAsync(startGatePath, "release");
            var relayedBody = await ReadWhenReadyAsync(publicPort);
            var directBody = await ReadWhenReadyAsync(privatePort);
            var status = await ReadStatusAsync("running");
            var lease = new BackendLeaseStore(
                ContinuityPaths.BackendLeaseFile(root)).Load();

            Assert.NotEqual(publicPort, privatePort);
            Assert.Equal($"backend:{privatePort}", relayedBody);
            Assert.Equal(relayedBody, directBody);
            Assert.Equal(
                new SupervisorStatus(
                    State: "running",
                    SupervisorProcessId: Environment.ProcessId,
                    BackendProcessId: backendProcessId,
                    Port: publicPort,
                    CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                    ConsecutiveFailures: 0,
                    LastExitCode: null,
                    UpdatedAtUtc: status.UpdatedAtUtc,
                    NextRetryAtUtc: null,
                    Detail:
                        $"Relaying {LoopbackEndpoint.WebSocketUrl(publicPort)} to an owned private backend.",
                    SupervisorStartedAtUtc: status.SupervisorStartedAtUtc,
                    SupervisorExecutable: status.SupervisorExecutable),
                status);
            Assert.Equal(
                new BackendLeaseLoadResult(
                    BackendLeaseLoadKind.Loaded,
                    new BackendLease(
                        BackendLease.CurrentSchemaVersion,
                        OwnerSupervisorProcessId: Environment.ProcessId,
                        BackendProcessId: backendProcessId,
                        PublicPort: publicPort,
                        BackendPort: privatePort,
                        BackendExecutable: backendExecutable,
                        CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                        BackendStartedAtUtc: backendStartedAtUtc)),
                lease);
        }
        finally
        {
            shutdown.Cancel();
            await supervisor.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.False(ProcessIsRunning(backendProcessId));
        Assert.True(CanBind(publicPort));
        Assert.Equal(
            "stopped",
            new SupervisorStatusStore(ContinuityPaths.SupervisorStatusFile(root)).Read()?.State);
        Assert.Equal(
            BackendLeaseLoadKind.Missing,
            new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root)).Load().Kind);
    }

    [Fact]
    public async Task StatusWriteFailureStopsOwnedBackend()
    {
        Directory.CreateDirectory(ContinuityPaths.SupervisorStatusFile(root));
        var publicPort = FindAvailablePort();
        var backendProcessId = 0;

        WindowsProcessGroup StartBackend(int port)
        {
            var process = StartHarnessBackend(port, Path.Combine(root, "fixture-started.txt"));
            backendProcessId = process.Id;
            return process;
        }

        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await OwnedSupervisorRuntime.RunAsync(
                publicPort, root, CancellationToken.None, StartBackend)
                .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.False(ProcessIsRunning(backendProcessId));
        Assert.True(CanBind(publicPort));
        Assert.Equal(
            BackendLeaseLoadKind.Missing,
            new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root)).Load().Kind);
    }

    [Fact]
    public async Task RecoversVerifiedBackendWithoutStartingReplacement()
    {
        Directory.CreateDirectory(root);
        var publicPort = FindAvailablePort();
        var backendPort = FindAvailablePort(publicPort);
        var original = StartHarnessBackend(
            backendPort,
            Path.Combine(root, "recovered-started.txt"));
        var originalLease = LeaseFor(original, publicPort, backendPort) with
        {
            OwnerSupervisorProcessId = int.MaxValue,
        };
        await ReadWhenReadyAsync(backendPort);
        var leaseStore = new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root));
        leaseStore.Write(originalLease);
        original.Dispose();
        var replacementsStarted = 0;
        using var shutdown = new CancellationTokenSource();

        var supervisor = OwnedSupervisorRuntime.RunAsync(
            publicPort,
            root,
            shutdown.Token,
            port =>
            {
                Interlocked.Increment(ref replacementsStarted);
                return StartHarnessBackend(
                    port,
                    Path.Combine(root, "unexpected-replacement.txt"));
            });
        try
        {
            Assert.Equal($"backend:{backendPort}", await ReadWhenReadyAsync(publicPort));
            var status = await ReadStatusAsync("running");
            Assert.Equal(
                new SupervisorStatus(
                    State: "running",
                    SupervisorProcessId: Environment.ProcessId,
                    BackendProcessId: originalLease.BackendProcessId,
                    Port: publicPort,
                    CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                    ConsecutiveFailures: 0,
                    LastExitCode: null,
                    UpdatedAtUtc: status.UpdatedAtUtc,
                    NextRetryAtUtc: null,
                    Detail:
                        "Recovered the verified private backend behind the stable endpoint.",
                    SupervisorStartedAtUtc: status.SupervisorStartedAtUtc,
                    SupervisorExecutable: status.SupervisorExecutable),
                status);
            Assert.Equal(0, Volatile.Read(ref replacementsStarted));
            Assert.Equal(
                new BackendLeaseLoadResult(
                    BackendLeaseLoadKind.Loaded,
                    originalLease with { OwnerSupervisorProcessId = Environment.ProcessId }),
                leaseStore.Load());
        }
        finally
        {
            shutdown.Cancel();
            Assert.Equal(0, await supervisor.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        Assert.False(ProcessIsRunning(originalLease.BackendProcessId));
        Assert.Equal(BackendLeaseLoadKind.Missing, leaseStore.Load().Kind);
        Assert.True(CanBind(publicPort));
    }

    [Fact]
    public async Task RecoveredBackendUnavailablePreservesProcessAndLease()
    {
        Directory.CreateDirectory(root);
        var publicPort = FindAvailablePort();
        var backendPort = FindAvailablePort(publicPort);
        var startGatePath = Path.Combine(root, "recovered-start-gate.txt");
        var fixtureStartedPath = Path.Combine(root, "recovered-started.txt");
        var original = StartHarnessBackend(backendPort, fixtureStartedPath, startGatePath);
        var originalLease = LeaseFor(original, publicPort, backendPort);
        await WaitForFileAsync(fixtureStartedPath);
        await AssertBackendNotReadyAsync(backendPort);
        var leaseStore = new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root));
        leaseStore.Write(originalLease);
        original.Dispose();
        var replacementsStarted = 0;

        var exitCode = await OwnedSupervisorRuntime.RunAsync(
                publicPort,
                root,
                CancellationToken.None,
                _ =>
                {
                    Interlocked.Increment(ref replacementsStarted);
                    throw new InvalidOperationException("The replacement callback must not run.");
                },
                readinessTimeout: TimeSpan.FromMilliseconds(250))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, exitCode);
        Assert.Equal(0, Volatile.Read(ref replacementsStarted));
        Assert.True(ProcessIsRunning(originalLease.BackendProcessId));
        Assert.Equal(
            new BackendLeaseLoadResult(BackendLeaseLoadKind.Loaded, originalLease),
            leaseStore.Load());
        var status = await ReadStatusAsync("recoveredBackendUnavailable");
        Assert.Equal(
            new SupervisorStatus(
                State: "recoveredBackendUnavailable",
                SupervisorProcessId: Environment.ProcessId,
                BackendProcessId: originalLease.BackendProcessId,
                Port: publicPort,
                CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                ConsecutiveFailures: 0,
                LastExitCode: null,
                UpdatedAtUtc: status.UpdatedAtUtc,
                NextRetryAtUtc: null,
                Detail: "The verified recovered backend is not ready; refusing to replace it.",
                SupervisorStartedAtUtc: status.SupervisorStartedAtUtc,
                SupervisorExecutable: status.SupervisorExecutable),
            status);
        Assert.True(CanBind(publicPort));
    }

    [Fact]
    public async Task RecoveredBackendSurvivesSupervisorStatusWriteFailure()
    {
        Directory.CreateDirectory(root);
        var publicPort = FindAvailablePort();
        var backendPort = FindAvailablePort(publicPort);
        var original = StartHarnessBackend(
            backendPort,
            Path.Combine(root, "recovered-started.txt"));
        var originalLease = LeaseFor(original, publicPort, backendPort) with
        {
            OwnerSupervisorProcessId = int.MaxValue,
        };
        await ReadWhenReadyAsync(backendPort);
        var leaseStore = new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root));
        leaseStore.Write(originalLease);
        original.Dispose();
        Directory.CreateDirectory(ContinuityPaths.SupervisorStatusFile(root));
        var replacementsStarted = 0;

        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await OwnedSupervisorRuntime.RunAsync(
                    publicPort,
                    root,
                    CancellationToken.None,
                    _ =>
                    {
                        Interlocked.Increment(ref replacementsStarted);
                        throw new InvalidOperationException(
                            "The replacement callback must not run.");
                    })
                .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(0, Volatile.Read(ref replacementsStarted));
        Assert.True(ProcessIsRunning(originalLease.BackendProcessId));
        Assert.Equal(
            new BackendLeaseLoadResult(
                BackendLeaseLoadKind.Loaded,
                originalLease with { OwnerSupervisorProcessId = Environment.ProcessId }),
            leaseStore.Load());
        Assert.True(CanBind(publicPort));
        Assert.False(CanBind(backendPort));
    }

    [Fact]
    public async Task CancellationDuringRecoveredReadinessStopsBackendAndDeletesLease()
    {
        Directory.CreateDirectory(root);
        var publicPort = FindAvailablePort();
        var backendPort = FindAvailablePort(publicPort);
        var startGatePath = Path.Combine(root, "recovered-start-gate.txt");
        var fixtureStartedPath = Path.Combine(root, "recovered-started.txt");
        var original = StartHarnessBackend(backendPort, fixtureStartedPath, startGatePath);
        var originalLease = LeaseFor(original, publicPort, backendPort);
        await WaitForFileAsync(fixtureStartedPath);
        await AssertBackendNotReadyAsync(backendPort);
        var leaseStore = new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root));
        leaseStore.Write(originalLease);
        original.Dispose();
        var replacementsStarted = 0;
        using var shutdown = new CancellationTokenSource();

        var supervisor = OwnedSupervisorRuntime.RunAsync(
            publicPort,
            root,
            shutdown.Token,
            _ =>
            {
                Interlocked.Increment(ref replacementsStarted);
                throw new InvalidOperationException("The replacement callback must not run.");
            },
            readinessTimeout: TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => !CanBind(publicPort),
            "Supervisor did not bind the gated public endpoint.");
        shutdown.Cancel();

        Assert.Equal(0, await supervisor.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, Volatile.Read(ref replacementsStarted));
        Assert.False(ProcessIsRunning(originalLease.BackendProcessId));
        Assert.Equal(BackendLeaseLoadKind.Missing, leaseStore.Load().Kind);
        Assert.True(CanBind(publicPort));
        await WaitUntilAsync(
            () => CanBind(backendPort),
            "Recovered backend did not release its private endpoint.");
    }

    [Fact]
    public async Task InvalidBackendLeaseFailsClosedBeforeStartingRelayOrBackend()
    {
        Directory.CreateDirectory(root);
        var leasePath = ContinuityPaths.BackendLeaseFile(root);
        File.WriteAllText(leasePath, "{not-json");
        var malformedLease = File.ReadAllBytes(leasePath);
        var publicPort = FindAvailablePort();
        var startCount = 0;

        var exitCode = await OwnedSupervisorRuntime.RunAsync(
            publicPort,
            root,
            CancellationToken.None,
            _ =>
            {
                Interlocked.Increment(ref startCount);
                throw new InvalidOperationException("The backend callback must not run.");
            });

        Assert.Equal(1, exitCode);
        Assert.Equal(0, Volatile.Read(ref startCount));
        Assert.Equal(malformedLease, File.ReadAllBytes(leasePath));
        var status = await ReadStatusAsync("unsafeBackendLease");
        Assert.Equal(
            new SupervisorStatus(
                State: "unsafeBackendLease",
                SupervisorProcessId: Environment.ProcessId,
                BackendProcessId: null,
                Port: publicPort,
                CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                ConsecutiveFailures: 0,
                LastExitCode: null,
                UpdatedAtUtc: status.UpdatedAtUtc,
                NextRetryAtUtc: null,
                Detail: "The persisted backend lease is invalid.",
                SupervisorStartedAtUtc: status.SupervisorStartedAtUtc,
                SupervisorExecutable: status.SupervisorExecutable),
            status);
        Assert.True(CanBind(publicPort));
    }

    [Fact]
    public async Task ForeignPrivateListenerNeverOpensPublicGate()
    {
        var publicPort = FindAvailablePort();
        var privatePort = 0;
        var trackedProcessId = 0;
        var startCount = 0;
        WindowsProcessGroup? foreign = null;
        using var shutdown = new CancellationTokenSource();
        var supervisor = OwnedSupervisorRuntime.RunAsync(
            publicPort,
            root,
            shutdown.Token,
            port =>
            {
                if (Interlocked.Increment(ref startCount) != 1)
                {
                    throw new InvalidOperationException("The backend callback ran more than once.");
                }
                privatePort = port;
                foreign = StartHarnessBackend(
                    port,
                    Path.Combine(root, "foreign-started.txt"));
                var tracked = StartIdleProcess(
                    Path.Combine(root, "tracked-started.txt"));
                trackedProcessId = tracked.Id;
                return tracked;
            });
        try
        {
            var exitCode = await supervisor.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, exitCode);
            Assert.Equal(1, Volatile.Read(ref startCount));
            Assert.NotNull(foreign);
            Assert.False(foreign.HasExited);
            Assert.False(ProcessIsRunning(trackedProcessId));
            Assert.True(CanBind(publicPort));
            Assert.False(CanBind(privatePort));
            Assert.Equal(
                BackendLeaseLoadKind.Missing,
                new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root)).Load().Kind);
            var status = await ReadStatusAsync("backendOwnershipLost");
            Assert.Equal(
                new SupervisorStatus(
                    State: "backendOwnershipLost",
                    SupervisorProcessId: Environment.ProcessId,
                    BackendProcessId: trackedProcessId,
                    Port: publicPort,
                    CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                    ConsecutiveFailures: 0,
                    LastExitCode: null,
                    UpdatedAtUtc: status.UpdatedAtUtc,
                    NextRetryAtUtc: null,
                    Detail: "The private listener is not owned by the supervised backend.",
                    SupervisorStartedAtUtc: status.SupervisorStartedAtUtc,
                    SupervisorExecutable: status.SupervisorExecutable),
                status);
        }
        finally
        {
            shutdown.Cancel();
            try
            {
                await supervisor.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                if (foreign is { HasExited: false })
                {
                    foreign.Kill();
                    await foreign.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                foreign?.Dispose();
            }
        }

        Assert.True(CanBind(privatePort));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelayAdmissionRejectsMismatchWithoutForwardingBytes(bool afterConnect)
    {
        var publicPort = FindAvailablePort();
        var backendProcessId = 0;
        var listenerDenied = 0;
        var connectionDenied = 0;
        var listenerChecks = 0;
        var connectionChecks = 0;
        var requestLogPath = Path.Combine(root, "backend-requests.txt");
        var ownershipChecks = new BackendOwnershipChecks(
            (_, processId) =>
            {
                Interlocked.Increment(ref listenerChecks);
                return processId == Volatile.Read(ref backendProcessId) &&
                    Volatile.Read(ref listenerDenied) == 0;
            },
            (_, processId) =>
            {
                Interlocked.Increment(ref connectionChecks);
                return processId == Volatile.Read(ref backendProcessId) &&
                    Volatile.Read(ref connectionDenied) == 0;
            });
        using var shutdown = new CancellationTokenSource();

        WindowsProcessGroup StartBackend(int port)
        {
            var process = StartHarnessBackend(
                port,
                Path.Combine(root, "fixture-started.txt"),
                requestLogPath: requestLogPath);
            Volatile.Write(ref backendProcessId, process.Id);
            return process;
        }

        var supervisor = OwnedSupervisorRuntime.RunAsync(
            publicPort,
            root,
            shutdown.Token,
            StartBackend,
            ownershipChecks: ownershipChecks);
        try
        {
            await ReadStatusAsync("running");
            var baselineRequests = File.ReadAllLines(requestLogPath).Length;
            var baselineListenerChecks = Volatile.Read(ref listenerChecks);
            var baselineConnectionChecks = Volatile.Read(ref connectionChecks);
            if (afterConnect)
            {
                Volatile.Write(ref connectionDenied, 1);
            }
            else
            {
                Volatile.Write(ref listenerDenied, 1);
            }

            await AssertEndpointUnavailableAsync(publicPort);
            await WaitUntilAsync(
                () => Volatile.Read(ref listenerChecks) > baselineListenerChecks &&
                    (!afterConnect ||
                        Volatile.Read(ref connectionChecks) > baselineConnectionChecks),
                "Relay did not run the configured ownership admission checks.");
            await Task.Delay(100);

            Assert.Equal(
                baselineRequests,
                File.ReadAllLines(requestLogPath).Length);
            Assert.Equal(
                afterConnect ? baselineConnectionChecks + 1 : baselineConnectionChecks,
                Volatile.Read(ref connectionChecks));
            Assert.True(ProcessIsRunning(backendProcessId));
        }
        finally
        {
            shutdown.Cancel();
            Assert.Equal(0, await supervisor.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        Assert.False(ProcessIsRunning(backendProcessId));
        Assert.True(CanBind(publicPort));
        Assert.Equal(
            BackendLeaseLoadKind.Missing,
            new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root)).Load().Kind);
    }

    [Fact]
    public async Task UnknownOwnershipKillsNewBackendWithoutWritingLease()
    {
        var publicPort = FindAvailablePort();
        var backendProcessId = 0;
        var connectionChecks = 0;
        var ownershipChecks = UnavailableOwnershipChecks(
            () => Interlocked.Increment(ref connectionChecks));
        using var shutdown = new CancellationTokenSource();
        var supervisor = OwnedSupervisorRuntime.RunAsync(
            publicPort,
            root,
            shutdown.Token,
            port =>
            {
                var process = StartHarnessBackend(
                    port,
                    Path.Combine(root, "fixture-started.txt"));
                backendProcessId = process.Id;
                return process;
            },
            ownershipChecks: ownershipChecks);
        try
        {
            Assert.Equal(1, await supervisor.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(ProcessIsRunning(backendProcessId));
            Assert.Equal(0, Volatile.Read(ref connectionChecks));
            Assert.Equal(
                BackendLeaseLoadKind.Missing,
                new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root)).Load().Kind);
            var status = await ReadStatusAsync("backendOwnershipUnknown");
            Assert.Equal(
                new SupervisorStatus(
                    State: "backendOwnershipUnknown",
                    SupervisorProcessId: Environment.ProcessId,
                    BackendProcessId: backendProcessId,
                    Port: publicPort,
                    CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                    ConsecutiveFailures: 0,
                    LastExitCode: null,
                    UpdatedAtUtc: status.UpdatedAtUtc,
                    NextRetryAtUtc: null,
                    Detail: "Private listener ownership could not be inspected; " +
                        "refusing to publish the new backend.",
                    SupervisorStartedAtUtc: status.SupervisorStartedAtUtc,
                    SupervisorExecutable: status.SupervisorExecutable),
                status);
            Assert.True(CanBind(publicPort));
        }
        finally
        {
            shutdown.Cancel();
            await supervisor.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task UnknownOwnershipPreservesRecoveredBackendAndLease()
    {
        Directory.CreateDirectory(root);
        var publicPort = FindAvailablePort();
        var backendPort = FindAvailablePort(publicPort);
        var original = StartHarnessBackend(
            backendPort,
            Path.Combine(root, "recovered-started.txt"));
        var originalLease = LeaseFor(original, publicPort, backendPort) with
        {
            OwnerSupervisorProcessId = int.MaxValue,
        };
        await ReadWhenReadyAsync(backendPort);
        var leaseStore = new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root));
        leaseStore.Write(originalLease);
        original.Dispose();
        var replacementsStarted = 0;
        var connectionChecks = 0;
        var ownershipChecks = UnavailableOwnershipChecks(
            () => Interlocked.Increment(ref connectionChecks));
        using var shutdown = new CancellationTokenSource();
        var supervisor = OwnedSupervisorRuntime.RunAsync(
            publicPort,
            root,
            shutdown.Token,
            _ =>
            {
                Interlocked.Increment(ref replacementsStarted);
                throw new InvalidOperationException("The replacement callback must not run.");
            },
            ownershipChecks: ownershipChecks);
        try
        {
            Assert.Equal(1, await supervisor.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, Volatile.Read(ref replacementsStarted));
            Assert.Equal(0, Volatile.Read(ref connectionChecks));
            Assert.True(ProcessIsRunning(originalLease.BackendProcessId));
            Assert.Equal(
                new BackendLeaseLoadResult(BackendLeaseLoadKind.Loaded, originalLease),
                leaseStore.Load());
            var status = await ReadStatusAsync("backendOwnershipUnknown");
            Assert.Equal(
                new SupervisorStatus(
                    State: "backendOwnershipUnknown",
                    SupervisorProcessId: Environment.ProcessId,
                    BackendProcessId: originalLease.BackendProcessId,
                    Port: publicPort,
                    CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                    ConsecutiveFailures: 0,
                    LastExitCode: null,
                    UpdatedAtUtc: status.UpdatedAtUtc,
                    NextRetryAtUtc: null,
                    Detail: "Private listener ownership could not be inspected; " +
                        "preserving the recovered backend lease.",
                    SupervisorStartedAtUtc: status.SupervisorStartedAtUtc,
                    SupervisorExecutable: status.SupervisorExecutable),
                status);
            Assert.True(CanBind(publicPort));
        }
        finally
        {
            shutdown.Cancel();
            await supervisor.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task RecoveredOwnershipLossCleansUpWhenStatusWriteFails()
    {
        Directory.CreateDirectory(root);
        var publicPort = FindAvailablePort();
        var backendPort = FindAvailablePort(publicPort);
        var original = StartHarnessBackend(
            backendPort,
            Path.Combine(root, "recovered-started.txt"));
        var originalLease = LeaseFor(original, publicPort, backendPort) with
        {
            OwnerSupervisorProcessId = int.MaxValue,
        };
        await ReadWhenReadyAsync(backendPort);
        var leaseStore = new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root));
        leaseStore.Write(originalLease);
        original.Dispose();
        Directory.CreateDirectory(ContinuityPaths.SupervisorStatusFile(root));
        var replacementsStarted = 0;
        var ownershipChecks = new BackendOwnershipChecks(
            (_, _) => false,
            (_, _) => true);

        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await OwnedSupervisorRuntime.RunAsync(
                    publicPort,
                    root,
                    CancellationToken.None,
                    _ =>
                    {
                        Interlocked.Increment(ref replacementsStarted);
                        throw new InvalidOperationException("The replacement callback must not run.");
                    },
                    ownershipChecks: ownershipChecks)
                .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(0, Volatile.Read(ref replacementsStarted));
        Assert.False(ProcessIsRunning(originalLease.BackendProcessId));
        Assert.Equal(BackendLeaseLoadKind.Missing, leaseStore.Load().Kind);
        Assert.True(CanBind(publicPort));
    }

    [Fact]
    public async Task OngoingOwnershipLossClosesRelayAndStopsBackend()
    {
        var publicPort = FindAvailablePort();
        var backendPort = 0;
        var backendProcessId = 0;
        var ownershipLost = 0;
        var connectionChecks = 0;
        var ownershipChecks = new BackendOwnershipChecks(
            (_, processId) => processId == Volatile.Read(ref backendProcessId) &&
                Volatile.Read(ref ownershipLost) == 0,
            (_, processId) =>
            {
                Interlocked.Increment(ref connectionChecks);
                return processId == Volatile.Read(ref backendProcessId);
            })
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
        };
        using var shutdown = new CancellationTokenSource();
        var supervisor = OwnedSupervisorRuntime.RunAsync(
            publicPort,
            root,
            shutdown.Token,
            port =>
            {
                backendPort = port;
                var process = StartHarnessBackend(
                    port,
                    Path.Combine(root, "fixture-started.txt"));
                Volatile.Write(ref backendProcessId, process.Id);
                return process;
            },
            ownershipChecks: ownershipChecks);
        try
        {
            Assert.Equal($"backend:{backendPort}", await ReadWhenReadyAsync(publicPort));
            var baselineConnectionChecks = Volatile.Read(ref connectionChecks);
            using var heldConnection = await OpenHeldRelayConnectionAsync(
                publicPort,
                () => Volatile.Read(ref connectionChecks) > baselineConnectionChecks);
            var connectionClosed = WaitForConnectionClosedAsync(heldConnection);
            Volatile.Write(ref ownershipLost, 1);

            Assert.Same(
                connectionClosed,
                await Task.WhenAny(connectionClosed, supervisor).WaitAsync(TimeSpan.FromSeconds(10)));
            await connectionClosed;
            Assert.Equal(1, await supervisor.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(ProcessIsRunning(backendProcessId));
            Assert.Equal(
                BackendLeaseLoadKind.Missing,
                new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root)).Load().Kind);
            var status = await ReadStatusAsync("backendOwnershipLost");
            Assert.Equal(
                new SupervisorStatus(
                    State: "backendOwnershipLost",
                    SupervisorProcessId: Environment.ProcessId,
                    BackendProcessId: backendProcessId,
                    Port: publicPort,
                    CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                    ConsecutiveFailures: 0,
                    LastExitCode: null,
                    UpdatedAtUtc: status.UpdatedAtUtc,
                    NextRetryAtUtc: null,
                    Detail: "The private listener is no longer owned by the supervised backend.",
                    SupervisorStartedAtUtc: status.SupervisorStartedAtUtc,
                    SupervisorExecutable: status.SupervisorExecutable),
                status);
            Assert.True(CanBind(publicPort));
        }
        finally
        {
            shutdown.Cancel();
            await supervisor.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task OngoingUnknownOwnershipPreservesVerifiedBackendAndLease()
    {
        var publicPort = FindAvailablePort();
        var backendProcessId = 0;
        var inspectionUnavailable = 0;
        var connectionChecks = 0;
        var ownershipChecks = new BackendOwnershipChecks(
            (_, processId) => Volatile.Read(ref inspectionUnavailable) == 0
                ? processId == Volatile.Read(ref backendProcessId)
                : throw new Win32Exception(5, "ownership inspection unavailable"),
            (_, processId) =>
            {
                Interlocked.Increment(ref connectionChecks);
                return processId == Volatile.Read(ref backendProcessId);
            })
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
        };
        using var shutdown = new CancellationTokenSource();
        var leaseStore = new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root));
        var supervisor = OwnedSupervisorRuntime.RunAsync(
            publicPort,
            root,
            shutdown.Token,
            port =>
            {
                var process = StartHarnessBackend(
                    port,
                    Path.Combine(root, "fixture-started.txt"));
                Volatile.Write(ref backendProcessId, process.Id);
                return process;
            },
            ownershipChecks: ownershipChecks);
        try
        {
            await ReadStatusAsync("running");
            var verifiedLease = leaseStore.Load();
            Assert.Equal(BackendLeaseLoadKind.Loaded, verifiedLease.Kind);
            var baselineConnectionChecks = Volatile.Read(ref connectionChecks);
            using var heldConnection = await OpenHeldRelayConnectionAsync(
                publicPort,
                () => Volatile.Read(ref connectionChecks) > baselineConnectionChecks);
            var connectionClosed = WaitForConnectionClosedAsync(heldConnection);
            Volatile.Write(ref inspectionUnavailable, 1);

            Assert.Same(
                connectionClosed,
                await Task.WhenAny(connectionClosed, supervisor).WaitAsync(TimeSpan.FromSeconds(10)));
            await connectionClosed;
            Assert.Equal(1, await supervisor.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(ProcessIsRunning(backendProcessId));
            Assert.Equal(verifiedLease, leaseStore.Load());
            Assert.Equal(
                $"backend:{verifiedLease.Lease!.BackendPort}",
                await ReadWhenReadyAsync(verifiedLease.Lease.BackendPort));
            var status = await ReadStatusAsync("backendOwnershipUnknown");
            Assert.Equal(
                new SupervisorStatus(
                    State: "backendOwnershipUnknown",
                    SupervisorProcessId: Environment.ProcessId,
                    BackendProcessId: backendProcessId,
                    Port: publicPort,
                    CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                    ConsecutiveFailures: 0,
                    LastExitCode: null,
                    UpdatedAtUtc: status.UpdatedAtUtc,
                    NextRetryAtUtc: null,
                    Detail: "Private listener ownership could not be inspected; " +
                        "preserving the verified backend lease.",
                    SupervisorStartedAtUtc: status.SupervisorStartedAtUtc,
                    SupervisorExecutable: status.SupervisorExecutable),
                status);
            Assert.True(CanBind(publicPort));
        }
        finally
        {
            shutdown.Cancel();
            await supervisor.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task BackendRestartKeepsPublicEndpointGatedUntilReplacementIsReady()
    {
        var publicPort = FindAvailablePort();
        var backendPorts = new ConcurrentQueue<int>();
        var backendProcessIds = new ConcurrentQueue<int>();
        var secondStartGatePath = Path.Combine(root, "second-start-gate.txt");
        var backoffEntered = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBackoff = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = 0;
        using var shutdown = new CancellationTokenSource();

        WindowsProcessGroup StartBackend(int port)
        {
            var currentGeneration = Interlocked.Increment(ref generation);
            var process = StartHarnessBackend(
                port,
                Path.Combine(root, $"fixture-started-{currentGeneration}.txt"),
                startGatePath: currentGeneration == 2 ? secondStartGatePath : null,
                exitAfterRequests: currentGeneration == 1 ? 2 : 0);
            backendPorts.Enqueue(port);
            backendProcessIds.Enqueue(process.Id);
            return process;
        }

        async Task<bool> WaitForRestart(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            backoffEntered.TrySetResult(delay);
            try
            {
                await releaseBackoff.Task.WaitAsync(cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        var supervisor = OwnedSupervisorRuntime.RunAsync(
            publicPort,
            root,
            shutdown.Token,
            StartBackend,
            delayForFailure: _ => TimeSpan.FromSeconds(10),
            waitForRestart: WaitForRestart);
        try
        {
            var firstBody = await ReadWhenReadyAsync(publicPort);
            Assert.Equal(
                TimeSpan.FromSeconds(10),
                await backoffEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            var backingOff = await ReadStatusAsync("backingOff");
            Assert.NotNull(backingOff.NextRetryAtUtc);
            Assert.StartsWith("App-server exited after ", backingOff.Detail);
            Assert.Equal(
                new SupervisorStatus(
                    State: "backingOff",
                    SupervisorProcessId: Environment.ProcessId,
                    BackendProcessId: null,
                    Port: publicPort,
                    CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                    ConsecutiveFailures: 1,
                    LastExitCode: 17,
                    UpdatedAtUtc: backingOff.UpdatedAtUtc,
                    NextRetryAtUtc: backingOff.NextRetryAtUtc,
                    Detail: backingOff.Detail,
                    SupervisorStartedAtUtc: backingOff.SupervisorStartedAtUtc,
                    SupervisorExecutable: backingOff.SupervisorExecutable),
                backingOff);
            Assert.InRange(
                backingOff.NextRetryAtUtc.Value - backingOff.UpdatedAtUtc,
                TimeSpan.FromSeconds(9.5),
                TimeSpan.FromSeconds(10));
            Assert.Equal(1, Volatile.Read(ref generation));
            Assert.False(ProcessIsRunning(backendProcessIds.Single()));
            await AssertEndpointUnavailableAsync(publicPort);
            Assert.False(CanBind(publicPort));

            releaseBackoff.SetResult(true);
            await WaitForFileAsync(Path.Combine(root, "fixture-started-2.txt"));
            var ports = backendPorts.ToArray();
            Assert.Equal(2, ports.Length);
            Assert.NotEqual(ports[0], ports[1]);
            Assert.Equal($"backend:{ports[0]}", firstBody);
            await AssertBackendNotReadyAsync(ports[1]);
            await AssertEndpointUnavailableAsync(publicPort);

            await File.WriteAllTextAsync(secondStartGatePath, "release");
            Assert.Equal($"backend:{ports[1]}", await ReadWhenReadyAsync(publicPort));
        }
        finally
        {
            shutdown.Cancel();
            releaseBackoff.TrySetResult(true);
            Assert.Equal(0, await supervisor.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        Assert.All(backendProcessIds, processId => Assert.False(ProcessIsRunning(processId)));
        Assert.True(CanBind(publicPort));
    }

    [Fact]
    public async Task CancellationDuringBackoffDoesNotStartReplacement()
    {
        var publicPort = FindAvailablePort();
        var backoffEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startCount = 0;
        var backendProcessId = 0;
        using var shutdown = new CancellationTokenSource();

        WindowsProcessGroup StartBackend(int port)
        {
            Interlocked.Increment(ref startCount);
            var process = StartHarnessBackend(
                port,
                Path.Combine(root, "fixture-started.txt"),
                exitAfterRequests: 1);
            backendProcessId = process.Id;
            return process;
        }

        async Task<bool> WaitForRestart(
            TimeSpan _,
            CancellationToken cancellationToken)
        {
            backoffEntered.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        var supervisor = OwnedSupervisorRuntime.RunAsync(
            publicPort,
            root,
            shutdown.Token,
            StartBackend,
            delayForFailure: _ => TimeSpan.FromMinutes(1),
            waitForRestart: WaitForRestart);
        try
        {
            await backoffEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, Volatile.Read(ref startCount));
            Assert.Equal("backingOff", (await ReadStatusAsync("backingOff")).State);
            await AssertEndpointUnavailableAsync(publicPort);
        }
        finally
        {
            shutdown.Cancel();
            Assert.Equal(0, await supervisor.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        Assert.Equal(1, Volatile.Read(ref startCount));
        Assert.False(ProcessIsRunning(backendProcessId));
        Assert.True(CanBind(publicPort));
    }

    [Fact]
    public async Task ForeignPublicEndpointDoesNotStartBackend()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.ExclusiveAddressUse,
            true);
        listener.Start();
        var publicPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var startCount = 0;

        var exitCode = await OwnedSupervisorRuntime.RunAsync(
            publicPort,
            root,
            CancellationToken.None,
            _ =>
            {
                startCount++;
                throw new InvalidOperationException("The backend callback must not run.");
            });

        Assert.Equal(1, exitCode);
        Assert.Equal(0, startCount);
        var status = new SupervisorStatusStore(
            ContinuityPaths.SupervisorStatusFile(root)).Read();
        Assert.NotNull(status);
        Assert.Equal(
            new SupervisorStatus(
                State: "foreignEndpoint",
                SupervisorProcessId: Environment.ProcessId,
                BackendProcessId: null,
                Port: publicPort,
                CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
                ConsecutiveFailures: 0,
                LastExitCode: null,
                UpdatedAtUtc: status.UpdatedAtUtc,
                NextRetryAtUtc: null,
                Detail:
                    "An endpoint not owned by this supervisor already uses the configured port.",
                SupervisorStartedAtUtc: status.SupervisorStartedAtUtc,
                SupervisorExecutable: status.SupervisorExecutable),
            status);
        Assert.True(listener.Server.IsBound);
        Assert.False(CanBind(publicPort));
        var accept = listener.AcceptTcpClientAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, publicPort);
        using var accepted = await accept.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(accepted.Connected);
    }

    private async Task<SupervisorStatus> ReadStatusAsync(string state)
    {
        var store = new SupervisorStatusStore(ContinuityPaths.SupervisorStatusFile(root));
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (store.Read() is { } status && status.State == state)
            {
                return status;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Supervisor did not publish {state} relay status.");
    }

    private WindowsProcessGroup StartHarnessBackend(
        int port,
        string fixtureStartedPath,
        string? startGatePath = null,
        int exitAfterRequests = 0,
        string? requestLogPath = null)
    {
        var executable = HarnessExecutable();
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        startInfo.ArgumentList.Add("fake-app-server");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add(fixtureStartedPath);
        startInfo.ArgumentList.Add(exitAfterRequests.ToString());
        if (startGatePath is not null || requestLogPath is not null)
        {
            startInfo.ArgumentList.Add(startGatePath ?? string.Empty);
        }
        if (requestLogPath is not null)
        {
            startInfo.ArgumentList.Add(requestLogPath);
        }
        var process = WindowsProcessGroup.Start(startInfo);
        harnesses.Enqueue(new(process.Id, process.StartedAtUtc, executable));
        return process;
    }

    private WindowsProcessGroup StartIdleProcess(string fixtureStartedPath)
    {
        var executable = HarnessExecutable();
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        startInfo.ArgumentList.Add("idle-process");
        startInfo.ArgumentList.Add(fixtureStartedPath);
        var process = WindowsProcessGroup.Start(startInfo);
        harnesses.Enqueue(new(process.Id, process.StartedAtUtc, executable));
        return process;
    }

    private static BackendOwnershipChecks UnavailableOwnershipChecks(
        Action connectionInspected) => new(
        (_, _) => throw new Win32Exception(5, "ownership inspection unavailable"),
        (_, _) =>
        {
            connectionInspected();
            return true;
        });

    private static BackendLease LeaseFor(
        WindowsProcessGroup process,
        int publicPort,
        int backendPort) => new(
        BackendLease.CurrentSchemaVersion,
        OwnerSupervisorProcessId: Environment.ProcessId,
        BackendProcessId: process.Id,
        PublicPort: publicPort,
        BackendPort: backendPort,
        BackendExecutable: process.ExecutablePath,
        CodexHome: FutureProcessEnvironment.ResolveCodexHome(),
        BackendStartedAtUtc: process.StartedAtUtc);

    private static string HarnessExecutable() => Path.ChangeExtension(
        typeof(HarnessMarker).Assembly.Location,
        ".exe");

    private static async Task WaitForFileAsync(string path)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (File.Exists(path))
            {
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Fixture did not create {path}.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string timeoutMessage)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException(timeoutMessage);
    }

    private static async Task AssertEndpointUnavailableAsync(int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.GetAsync($"http://127.0.0.1:{port}/readyz"));
    }

    private static async Task AssertBackendNotReadyAsync(int port)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            $"not-ready:{port}",
            await response.Content.ReadAsStringAsync());
    }

    private static async Task<TcpClient> OpenHeldRelayConnectionAsync(
        int port,
        Func<bool> reachedBackend)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            await client.GetStream().WriteAsync("GET /readyz HTTP/1.1\r\nX-Held: "u8.ToArray());
            await WaitUntilAsync(
                reachedBackend,
                "The held relay connection did not reach the verified backend.");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task WaitForConnectionClosedAsync(TcpClient client)
    {
        var buffer = new byte[1];
        try
        {
            var bytesRead = await client.GetStream().ReadAsync(buffer)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, bytesRead);
        }
        catch (Exception exception) when (
            exception is IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task<string> ReadWhenReadyAsync(int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                return await client.GetStringAsync($"http://127.0.0.1:{port}/readyz");
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(100);
            }
        }
        var status = new SupervisorStatusStore(
            ContinuityPaths.SupervisorStatusFile(root)).Read();
        var logPath = ContinuityPaths.AppServerLogFile(root);
        var log = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : "<no log>";
        throw new TimeoutException(
            $"Endpoint on port {port} did not become ready. Status: {status}. Log: {log}");
    }

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

    private static bool CanBind(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Server.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ExclusiveAddressUse,
                true);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static bool ProcessIsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var identity in harnesses)
        {
            StopHarnessIfMatching(identity);
        }
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void StopHarnessIfMatching(HarnessIdentity identity)
    {
        try
        {
            using var process = WindowsProcessGroup.Attach(identity.ProcessId);
            if (process.StartedAtUtc != identity.StartedAtUtc ||
                !StringComparer.OrdinalIgnoreCase.Equals(
                    process.ExecutablePath,
                    identity.ExecutablePath))
            {
                return;
            }
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5))
                    .GetAwaiter().GetResult();
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
        }
    }

    private sealed record HarnessIdentity(
        int ProcessId,
        DateTimeOffset StartedAtUtc,
        string ExecutablePath);
}
