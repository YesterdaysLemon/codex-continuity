using CodexContinuity;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class LoopbackRelayTests
{
    [Fact]
    public async Task ClosedGateCanVerifyAConnectingClientWithoutForwardingIt()
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(
            publicPort,
            backend.Port,
            startGated: true,
            gatedClientAdmission: _ => true);

        using var client = await ConnectAsync(publicPort);
        await relay.WaitForVerifiedGatedClientAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(relay.IsGated);
        Assert.Equal(0, relay.ActiveConnectionCount);
        await AssertConnectionClosedAsync(client);
    }

    [Fact]
    public async Task RelaysBidirectionalTrafficOnLoopback()
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        using var client = await ConnectAsync(publicPort);

        var response = await RoundTripAsync(client, "hello");

        Assert.Equal("backend:hello", response);
        Assert.Equal(1, relay.ActiveConnectionCount);
        Assert.False(relay.IsGated);
    }

    [Fact]
    public async Task ClosedGateDrainsConnectionsAndAllowsBackendSwap()
    {
        await using var firstBackend = new TaggedBackend("first:");
        await using var secondBackend = new TaggedBackend("second:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, firstBackend.Port);
        using var firstClient = await ConnectAsync(publicPort);
        Assert.Equal("first:before", await RoundTripAsync(firstClient, "before"));

        await relay.CloseGateAsync();

        Assert.True(relay.IsGated);
        Assert.Equal(0, relay.ActiveConnectionCount);
        await AssertConnectionClosedAsync(firstClient);
        relay.SetBackendPort(secondBackend.Port);
        relay.OpenGate();
        using var secondClient = await ConnectAsync(publicPort);
        Assert.Equal("second:after", await RoundTripAsync(secondClient, "after"));
    }

    [Fact]
    public async Task TransitionRecomputesOnlyAfterGateDrainAndKeepsSafePlanGated()
    {
        await using var backend = new TaggedBackend("backend:");
        await using var replacement = new TaggedBackend("replacement:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        using var activeClient = await ConnectAsync(publicPort);
        Assert.Equal("backend:before", await RoundTripAsync(activeClient, "before"));
        var safePlan = Plan(transitionReady: true);

        var result = await GatedHandoffTransition.CloseAndRecomputeAsync(
            relay,
            async _ =>
            {
                Assert.True(relay.IsGated);
                Assert.Equal(0, relay.ActiveConnectionCount);
                await AssertConnectionClosedAsync(activeClient);
                using var refused = await ConnectAsync(publicPort);
                await AssertConnectionClosedAsync(refused);
                return safePlan;
            });

        Assert.Equal(safePlan, result.Plan);
        Assert.NotNull(result.GateLease);
        Assert.True(relay.IsGated);
        Assert.Throws<InvalidOperationException>(relay.OpenGate);
        Assert.Throws<InvalidOperationException>(() => relay.SetBackendPort(replacement.Port));
        using var stillRefused = await ConnectAsync(publicPort);
        await AssertConnectionClosedAsync(stillRefused);
        using var privateClient = await ConnectAsync(backend.Port);
        Assert.Equal("backend:private", await RoundTripAsync(privateClient, "private"));
        Assert.True(result.GateLease.TryRetargetAndOpen(replacement.Port));
        using var replacementClient = await ConnectAsync(publicPort);
        Assert.Equal(
            "replacement:continued",
            await RoundTripAsync(replacementClient, "continued"));
        Assert.False(result.GateLease.TryOpen());
        Assert.False(result.GateLease.TryRetargetAndOpen(backend.Port));
    }

    [Fact]
    public async Task BlockedTransitionReopensRelayWithoutStoppingBackend()
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var blockedPlan = Plan(transitionReady: false);

        var result = await GatedHandoffTransition.CloseAndRecomputeAsync(
            relay,
            _ =>
            {
                Assert.True(relay.IsGated);
                return Task.FromResult(blockedPlan);
            });

        Assert.Equal(blockedPlan, result.Plan);
        Assert.Null(result.GateLease);
        Assert.False(relay.IsGated);
        using var client = await ConnectAsync(publicPort);
        Assert.Equal("backend:resumed", await RoundTripAsync(client, "resumed"));
    }

    [Fact]
    public async Task BlockedTransitionCannotReopenAConcurrentSafetyGate()
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var recomputationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRecomputation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var transition = GatedHandoffTransition.CloseAndRecomputeAsync(
            relay,
            async _ =>
            {
                recomputationStarted.SetResult();
                await finishRecomputation.Task;
                return Plan(transitionReady: false);
            });
        await recomputationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await relay.CloseGateAsync();
        finishRecomputation.SetResult();

        var result = await transition.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Plan.TransitionReady);
        Assert.Null(result.GateLease);
        Assert.True(relay.IsGated);
        using var refused = await ConnectAsync(publicPort);
        await AssertConnectionClosedAsync(refused);
    }

    [Fact]
    public async Task TransitionRejectsAGateAlreadyOwnedByAnotherSafetyBoundary()
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port, startGated: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GatedHandoffTransition.CloseAndRecomputeAsync(
                relay,
                _ => Task.FromResult(Plan(transitionReady: false))));

        Assert.True(relay.IsGated);
        using var refused = await ConnectAsync(publicPort);
        await AssertConnectionClosedAsync(refused);
    }

    [Fact]
    public async Task SafeTransitionLeaseCannotReopenAConcurrentSafetyGate()
    {
        await using var backend = new TaggedBackend("backend:");
        await using var replacement = new TaggedBackend("replacement:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var result = await GatedHandoffTransition.CloseAndRecomputeAsync(
            relay,
            _ => Task.FromResult(Plan(transitionReady: true)));
        Assert.NotNull(result.GateLease);

        await relay.CloseGateAsync();

        Assert.False(result.GateLease.TryOpen());
        Assert.False(result.GateLease.TryRetargetAndOpen(replacement.Port));
        Assert.True(relay.IsGated);
        using var refused = await ConnectAsync(publicPort);
        await AssertConnectionClosedAsync(refused);
        relay.OpenGate();
        using var originalClient = await ConnectAsync(publicPort);
        Assert.Equal("backend:unchanged", await RoundTripAsync(originalClient, "unchanged"));
    }

    [Fact]
    public async Task BackendStopReservationBlocksLeaseReopenAndRetarget()
    {
        await using var backend = new TaggedBackend("backend:");
        await using var replacement = new TaggedBackend("replacement:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var result = await GatedHandoffTransition.CloseAndRecomputeAsync(
            relay,
            _ => Task.FromResult(Plan(transitionReady: true)));
        Assert.NotNull(result.GateLease);

        using var reservation = result.GateLease.TryReserveBackendStop();

        Assert.NotNull(reservation);
        Assert.Equal(backend.Port, reservation.BackendPort);
        Assert.True(reservation.IsCurrent);
        Assert.False(result.GateLease.TryOpen());
        Assert.False(result.GateLease.TryRetargetAndOpen(replacement.Port));
        reservation.Dispose();
        using var nextReservation = result.GateLease.TryReserveBackendStop();
        Assert.NotNull(nextReservation);
        Assert.False(reservation.IsCurrent);
        Assert.True(nextReservation.IsCurrent);
        nextReservation.Dispose();
        Assert.True(result.GateLease.TryRetargetAndOpen(replacement.Port));
    }

    [Fact]
    public async Task ConcurrentGateCloseCannotReopenUntilStopReservationIsReleased()
    {
        await using var backend = new TaggedBackend("backend:");
        await using var replacement = new TaggedBackend("replacement:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var result = await GatedHandoffTransition.CloseAndRecomputeAsync(
            relay,
            _ => Task.FromResult(Plan(transitionReady: true)));
        Assert.NotNull(result.GateLease);
        using var reservation = result.GateLease.TryReserveBackendStop();
        Assert.NotNull(reservation);

        await relay.CloseGateAsync();

        Assert.False(reservation.IsCurrent);
        Assert.False(result.GateLease.TryOpen());
        Assert.False(result.GateLease.TryRetargetAndOpen(replacement.Port));
        Assert.Throws<InvalidOperationException>(relay.OpenGate);
        Assert.Throws<InvalidOperationException>(() => relay.SetBackendPort(replacement.Port));
        reservation.Dispose();
        relay.SetBackendPort(replacement.Port);
        relay.OpenGate();
        using var replacementClient = await ConnectAsync(publicPort);
        Assert.Equal(
            "replacement:continued",
            await RoundTripAsync(replacementClient, "continued"));
    }

    [Fact]
    public async Task FailedRecomputationReopensRelayWithoutStoppingBackend()
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);

        await Assert.ThrowsAsync<IOException>(() =>
            GatedHandoffTransition.CloseAndRecomputeAsync(
                relay,
                _ => throw new IOException("private observation failed")));

        Assert.False(relay.IsGated);
        using var client = await ConnectAsync(publicPort);
        Assert.Equal("backend:resumed", await RoundTripAsync(client, "resumed"));
    }

    [Fact]
    public async Task TimedOutRecomputationReopensRelayWithoutStoppingBackend()
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCancellation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var transition = GatedHandoffTransition.CloseAndRecomputeAsync(
            relay,
            async cancellationToken =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return Plan(transitionReady: true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.SetResult();
                    await finishCancellation.Task;
                    throw;
                }
            },
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromSeconds(5));

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(transition.IsCompleted);
        Assert.True(relay.IsGated);
        using (var refused = await ConnectAsync(publicPort))
        {
            await AssertConnectionClosedAsync(refused);
        }

        finishCancellation.SetResult();
        await Assert.ThrowsAsync<TimeoutException>(() => transition);
        Assert.False(relay.IsGated);
        using var client = await ConnectAsync(publicPort);
        Assert.Equal("backend:resumed", await RoundTripAsync(client, "resumed"));
    }

    [Fact]
    public async Task UncooperativeTimedOutRecomputationLeavesRelayGated()
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var neverCompletes = new TaskCompletionSource<ContinuityHandoffPlan>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var transition = GatedHandoffTransition.CloseAndRecomputeAsync(
            relay,
            _ => neverCompletes.Task,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(20));
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            transition.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("relay remains gated", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(relay.IsGated);
        using var refused = await ConnectAsync(publicPort);
        await AssertConnectionClosedAsync(refused);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public async Task RejectsUnboundedRecomputationTimeoutBeforeGating(int seconds)
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            GatedHandoffTransition.CloseAndRecomputeAsync(
                relay,
                _ => Task.FromResult(Plan(transitionReady: true)),
                TimeSpan.FromSeconds(seconds)));

        Assert.False(relay.IsGated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task RejectsUnboundedCancellationDrainTimeoutBeforeGating(int seconds)
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            GatedHandoffTransition.CloseAndRecomputeAsync(
                relay,
                _ => Task.FromResult(Plan(transitionReady: true)),
                cancellationDrainTimeout: TimeSpan.FromSeconds(seconds)));

        Assert.False(relay.IsGated);
    }

    [Fact]
    public async Task GateRefusesNewConnectionsUntilOpened()
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(
            publicPort,
            backend.Port,
            startGated: true);

        using (var refused = await ConnectAsync(publicPort))
        {
            await AssertConnectionClosedAsync(refused);
        }

        relay.OpenGate();
        using var accepted = await ConnectAsync(publicPort);
        Assert.Equal("backend:ready", await RoundTripAsync(accepted, "ready"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionRejectionFailsClosedAndAcceptLoopSurvives(bool afterConnect)
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        var denyEnabled = 1;
        var checks = 0;
        var reports = new ConcurrentQueue<Exception>();
        await using var relay = LoopbackRelay.Start(
            publicPort,
            backend.Port,
            reportError: reports.Enqueue,
            backendAdmission: (_, connectedBackend) =>
            {
                Interlocked.Increment(ref checks);
                return Volatile.Read(ref denyEnabled) == 0 ||
                    (connectedBackend is not null) != afterConnect;
            });

        using var refused = await ConnectAsync(publicPort);

        await AssertConnectionClosedAsync(refused);
        await WaitUntilAsync(() => relay.ActiveConnectionCount == 0);
        Assert.Equal(afterConnect ? 2 : 1, Volatile.Read(ref checks));
        Assert.Empty(reports);

        Volatile.Write(ref denyEnabled, 0);
        using var accepted = await ConnectAsync(publicPort);
        Assert.Equal("backend:ready", await RoundTripAsync(accepted, "ready"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionExceptionsAreReportedAndAcceptLoopSurvives(bool afterConnect)
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        var throwEnabled = 1;
        var reports = new ConcurrentQueue<Exception>();
        await using var relay = LoopbackRelay.Start(
            publicPort,
            backend.Port,
            reportError: reports.Enqueue,
            backendAdmission: (_, connectedBackend) =>
            {
                if (Volatile.Read(ref throwEnabled) == 1 &&
                    (connectedBackend is not null) == afterConnect)
                {
                    throw new IOException("admission failed");
                }
                return true;
            });

        using var refused = await ConnectAsync(publicPort);
        await AssertConnectionClosedAsync(refused);
        await WaitUntilAsync(() => relay.ActiveConnectionCount == 0);
        Assert.Single(reports);

        Volatile.Write(ref throwEnabled, 0);
        using var accepted = await ConnectAsync(publicPort);
        Assert.Equal("backend:ready", await RoundTripAsync(accepted, "ready"));
    }

    [Fact]
    public async Task ConnectionLimitFailsClosed()
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(
            publicPort,
            backend.Port,
            options: new LoopbackRelayOptions(MaximumConnections: 1));
        using var first = await ConnectAsync(publicPort);
        Assert.Equal("backend:one", await RoundTripAsync(first, "one"));

        using var refused = await ConnectAsync(publicPort);

        await AssertConnectionClosedAsync(refused);
        Assert.Equal(1, relay.ActiveConnectionCount);
    }

    [Fact]
    public async Task ConcurrentGateCloseIsIdempotent()
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        using var client = await ConnectAsync(publicPort);
        Assert.Equal("backend:open", await RoundTripAsync(client, "open"));

        await Task.WhenAll(relay.CloseGateAsync(), relay.CloseGateAsync());

        Assert.True(relay.IsGated);
        Assert.Equal(0, relay.ActiveConnectionCount);
        await AssertConnectionClosedAsync(client);
    }

    [Fact]
    public async Task GateCloseRejectsConcurrentAdmissions()
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        var contenders = await Task.WhenAll(Enumerable.Range(0, 32).Select(
            _ => ConnectAsync(publicPort)));
        try
        {
            await WaitUntilAsync(() => relay.ActiveConnectionCount > 0);
            await relay.CloseGateAsync();

            Assert.True(relay.IsGated);
            Assert.Equal(0, relay.ActiveConnectionCount);
            await Task.WhenAll(contenders.Select(AssertConnectionClosedAsync));
            using var refused = await ConnectAsync(publicPort);
            await AssertConnectionClosedAsync(refused);
        }
        finally
        {
            foreach (var contender in contenders)
            {
                contender.Dispose();
            }
        }
    }

    [Fact]
    public async Task CanceledGateDrainStaysClosedUntilConnectionsFinish()
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        using var client = await ConnectAsync(publicPort);
        Assert.Equal("backend:open", await RoundTripAsync(client, "open"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            relay.CloseGateAsync(canceled.Token));

        Assert.True(relay.IsGated);
        await WaitUntilAsync(() => relay.ActiveConnectionCount == 0);
        await AssertConnectionClosedAsync(client);
    }

    [Fact]
    public async Task RelaysFragmentedPayloadLargerThanBuffer()
    {
        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(
            publicPort,
            backend.Port,
            options: new LoopbackRelayOptions(BufferBytes: 1024));
        using var client = await ConnectAsync(publicPort);
        var payload = new string('x', 8 * 1024);

        var response = await RoundTripAsync(client, payload);

        Assert.Equal("backend:" + payload, response);
    }

    [Fact]
    public async Task OwnsPublicPortExclusivelyAndReleasesItOnDispose()
    {
        await using var firstBackend = new TaggedBackend("first:");
        await using var secondBackend = new TaggedBackend("second:");
        var publicPort = AvailablePort();
        var first = LoopbackRelay.Start(publicPort, firstBackend.Port);
        try
        {
            Assert.Throws<SocketException>(() =>
                LoopbackRelay.Start(publicPort, secondBackend.Port));
        }
        finally
        {
            await first.DisposeAsync();
        }

        await using var replacement = LoopbackRelay.Start(publicPort, secondBackend.Port);
        using var client = await ConnectAsync(publicPort);
        Assert.Equal("second:ready", await RoundTripAsync(client, "ready"));
    }

    [Fact]
    public async Task BackendCanOnlyChangeBehindDrainedGate()
    {
        var options = new LoopbackRelayOptions();
        options.Validate();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LoopbackRelayOptions(MaximumConnections: 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LoopbackRelayOptions(MaximumConnections: 257).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LoopbackRelayOptions(BufferBytes: 1024 * 1024 + 1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LoopbackRelayOptions(ConnectTimeout: TimeSpan.FromSeconds(31)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LoopbackRelayOptions(GateDrainTimeout: TimeSpan.FromSeconds(31)).Validate());

        await using var backend = new TaggedBackend("backend:");
        var publicPort = AvailablePort();
        await using var relay = LoopbackRelay.Start(publicPort, backend.Port);
        using var client = await ConnectAsync(publicPort);
        await WaitUntilAsync(() => relay.ActiveConnectionCount == 1);
        Assert.Throws<InvalidOperationException>(relay.OpenGate);
        Assert.Throws<InvalidOperationException>(() => relay.SetBackendPort(AvailablePort()));
        await relay.CloseGateAsync();
        Assert.Throws<ArgumentException>(() => relay.SetBackendPort(publicPort));
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(5));
        return client;
    }

    private static ContinuityHandoffPlan Plan(bool transitionReady) => new(
        transitionReady ? "handoff" : "wait",
        transitionReady,
        BackendReady: true,
        UpdateState: "loaded",
        PendingUpdate: false,
        ThreadCount: 0,
        new HandoffBlockerCounts(0, 0, 0, 0, 0),
        Reasons: []);

    private static async Task<string> RoundTripAsync(TcpClient client, string request)
    {
        var stream = client.GetStream();
        await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(request), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var response = await ReadFrameAsync(stream, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.UTF8.GetString(response);
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, sizeof(int));
        for (var offset = 0; offset < frame.Length; offset += 127)
        {
            await stream.WriteAsync(
                frame.AsMemory(offset, Math.Min(127, frame.Length - offset)),
                cancellationToken);
        }
    }

    private static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > 1024 * 1024)
        {
            throw new InvalidDataException($"Invalid test frame length: {length}.");
        }
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private static async Task AssertConnectionClosedAsync(TcpClient client)
    {
        var buffer = new byte[1];
        try
        {
            var count = await client.GetStream().ReadAsync(buffer).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, count);
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private static int AvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    private sealed class TaggedBackend : IAsyncDisposable
    {
        private readonly string prefix;
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource shutdown = new();
        private readonly Task acceptLoop;
        private readonly List<Task> connections = [];

        internal TaggedBackend(string prefix)
        {
            this.prefix = prefix;
            listener.Start();
            acceptLoop = AcceptLoopAsync();
        }

        internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public async ValueTask DisposeAsync()
        {
            shutdown.Cancel();
            listener.Stop();
            try
            {
                await acceptLoop;
                await Task.WhenAll(connections);
            }
            finally
            {
                shutdown.Dispose();
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!shutdown.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(shutdown.Token);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }
                connections.Add(ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                try
                {
                    while (!shutdown.IsCancellationRequested)
                    {
                        byte[] request;
                        try
                        {
                            request = await ReadFrameAsync(stream, shutdown.Token);
                        }
                        catch (EndOfStreamException)
                        {
                            break;
                        }
                        var response = Encoding.UTF8.GetBytes(
                            prefix + Encoding.UTF8.GetString(request));
                        await WriteFrameAsync(stream, response, shutdown.Token);
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or SocketException or OperationCanceledException)
                {
                }
            }
        }
    }
}
