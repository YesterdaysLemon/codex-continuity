using CodexContinuity;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class LoopbackRelayTests
{
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

    private static async Task<string> RoundTripAsync(TcpClient client, string request)
    {
        var bytes = Encoding.UTF8.GetBytes(request);
        var stream = client.GetStream();
        await stream.WriteAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var response = new byte[256];
        var count = await stream.ReadAsync(response).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.UTF8.GetString(response, 0, count);
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
                connections.Add(ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[256];
                try
                {
                    while (!shutdown.IsCancellationRequested)
                    {
                        var count = await stream.ReadAsync(buffer, shutdown.Token);
                        if (count == 0)
                        {
                            break;
                        }
                        var request = Encoding.UTF8.GetString(buffer, 0, count);
                        var response = Encoding.UTF8.GetBytes(prefix + request);
                        await stream.WriteAsync(response, shutdown.Token);
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
