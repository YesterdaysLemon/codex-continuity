using System.Net;
using System.Net.Sockets;

namespace CodexContinuity;

internal sealed record LoopbackRelayOptions(
    int MaximumConnections = 64,
    int BufferBytes = 64 * 1024,
    TimeSpan? ConnectTimeout = null,
    TimeSpan? GateDrainTimeout = null)
{
    internal TimeSpan EffectiveConnectTimeout => ConnectTimeout ?? TimeSpan.FromSeconds(5);

    internal TimeSpan EffectiveGateDrainTimeout => GateDrainTimeout ?? TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        if (MaximumConnections is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConnections));
        }
        if (BufferBytes is < 1024 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(BufferBytes));
        }
        if (EffectiveConnectTimeout <= TimeSpan.Zero ||
            EffectiveConnectTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        }
        if (EffectiveGateDrainTimeout <= TimeSpan.Zero ||
            EffectiveGateDrainTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(GateDrainTimeout));
        }
    }
}

internal sealed class LoopbackRelay : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly TcpListener listener;
    private readonly LoopbackRelayOptions options;
    private readonly Action<Exception> reportError;
    private readonly CancellationTokenSource shutdown = new();
    private readonly HashSet<RelayConnection> connections = [];
    private readonly Task acceptLoop;
    private readonly int publicPort;
    private int backendPort;
    private bool gated;
    private bool disposed;

    private LoopbackRelay(
        int publicPort,
        int backendPort,
        bool startGated,
        LoopbackRelayOptions options,
        Action<Exception> reportError)
    {
        LoopbackEndpoint.ValidatePort(publicPort);
        LoopbackEndpoint.ValidatePort(backendPort);
        if (publicPort == backendPort)
        {
            throw new ArgumentException("Relay and backend ports must be different.");
        }
        options.Validate();

        this.publicPort = publicPort;
        this.backendPort = backendPort;
        this.options = options;
        this.reportError = reportError;
        gated = startGated;
        listener = new TcpListener(IPAddress.Loopback, publicPort);
        listener.Server.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.ExclusiveAddressUse,
            true);
        listener.Start(options.MaximumConnections);
        acceptLoop = AcceptLoopAsync();
    }

    internal static LoopbackRelay Start(
        int publicPort,
        int backendPort,
        bool startGated = false,
        LoopbackRelayOptions? options = null,
        Action<Exception>? reportError = null) =>
        new(
            publicPort,
            backendPort,
            startGated,
            options ?? new LoopbackRelayOptions(),
            reportError ?? (_ => { }));

    internal bool IsGated
    {
        get
        {
            lock (sync)
            {
                return gated;
            }
        }
    }

    internal int ActiveConnectionCount
    {
        get
        {
            lock (sync)
            {
                return connections.Count;
            }
        }
    }

    internal async Task CloseGateAsync(CancellationToken cancellationToken = default)
    {
        RelayConnection[] snapshot;
        lock (sync)
        {
            ThrowIfDisposed();
            gated = true;
            snapshot = [.. connections];
        }

        foreach (var connection in snapshot)
        {
            connection.Abort();
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (snapshot.Length == 0)
        {
            return;
        }

        await Task.WhenAll(snapshot.Select(connection => connection.Completion)).WaitAsync(
            options.EffectiveGateDrainTimeout,
            cancellationToken);
    }

    internal void SetBackendPort(int port)
    {
        LoopbackEndpoint.ValidatePort(port);
        lock (sync)
        {
            ThrowIfDisposed();
            if (port == publicPort)
            {
                throw new ArgumentException(
                    "Relay and backend ports must be different.",
                    nameof(port));
            }
            if (!gated || connections.Count != 0)
            {
                throw new InvalidOperationException(
                    "The relay backend can only change behind a closed, drained gate.");
            }
            backendPort = port;
        }
    }

    internal void OpenGate()
    {
        lock (sync)
        {
            ThrowIfDisposed();
            if (connections.Count != 0)
            {
                throw new InvalidOperationException(
                    "The relay gate cannot open until old connections have drained.");
            }
            gated = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        RelayConnection[] snapshot;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            gated = true;
            snapshot = [.. connections];
        }

        shutdown.Cancel();
        listener.Stop();
        foreach (var connection in snapshot)
        {
            connection.Abort();
        }

        try
        {
            await acceptLoop;
            await Task.WhenAll(snapshot.Select(connection => connection.Completion));
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

            RelayConnection? connection = null;
            lock (sync)
            {
                if (!disposed && !gated && connections.Count < options.MaximumConnections)
                {
                    connection = new RelayConnection(
                        client,
                        backendPort,
                        options,
                        ConnectionCompleted);
                    connections.Add(connection);
                }
            }

            if (connection is null)
            {
                client.Dispose();
            }
            else
            {
                connection.Start();
            }
        }
    }

    private void ConnectionCompleted(RelayConnection connection, Exception? error)
    {
        lock (sync)
        {
            connections.Remove(connection);
        }
        if (error is not null)
        {
            try
            {
                reportError(error);
            }
            catch
            {
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class RelayConnection(
        TcpClient client,
        int backendPort,
        LoopbackRelayOptions options,
        Action<RelayConnection, Exception?> completed)
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private TcpClient? backend;
        private int aborted;

        internal Task Completion => completion.Task;

        internal void Start() => _ = RunAsync();

        internal void Abort()
        {
            if (Interlocked.Exchange(ref aborted, 1) != 0)
            {
                return;
            }
            try
            {
                lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            client.Dispose();
            backend?.Dispose();
        }

        private async Task RunAsync()
        {
            Exception? error = null;
            try
            {
                client.NoDelay = true;
                backend = new TcpClient { NoDelay = true };
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    lifetime.Token);
                connectTimeout.CancelAfter(options.EffectiveConnectTimeout);
                await backend.ConnectAsync(
                    IPAddress.Loopback,
                    backendPort,
                    connectTimeout.Token);

                using var pumping = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var clientStream = client.GetStream();
                var backendStream = backend.GetStream();
                var upstream = clientStream.CopyToAsync(
                    backendStream,
                    options.BufferBytes,
                    pumping.Token);
                var downstream = backendStream.CopyToAsync(
                    clientStream,
                    options.BufferBytes,
                    pumping.Token);
                await Task.WhenAny(upstream, downstream);
                pumping.Cancel();
                await IgnoreExpectedClosureAsync(upstream, downstream);
            }
            catch (Exception exception) when (IsExpectedClosure(exception))
            {
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                Interlocked.Exchange(ref aborted, 1);
                client.Dispose();
                backend?.Dispose();
                lifetime.Dispose();
                try
                {
                    completed(this, error);
                }
                finally
                {
                    completion.TrySetResult();
                }
            }
        }

        private static async Task IgnoreExpectedClosureAsync(params Task[] tasks)
        {
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception exception) when (IsExpectedClosure(exception))
            {
            }
        }

        private static bool IsExpectedClosure(Exception exception) =>
            exception is IOException or SocketException or ObjectDisposedException or
                OperationCanceledException;
    }
}
