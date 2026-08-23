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

internal sealed class RelayGateLease(LoopbackRelay relay, long ownedEpoch)
{
    internal RelayBackendStopReservation? TryReserveBackendStop() =>
        relay.TryReserveBackendStop(ownedEpoch);

    internal bool TryOpen() => relay.TryOpenGate(ownedEpoch);

    internal bool TryRetargetAndOpen(int backendPort) =>
        relay.TryRetargetAndOpenGate(ownedEpoch, backendPort);
}

internal sealed class RelayBackendStopReservation(
    LoopbackRelay relay,
    long ownedEpoch,
    long reservationToken,
    int backendPort) : IDisposable
{
    private int disposed;

    internal int BackendPort => backendPort;

    internal bool IsCurrent => Volatile.Read(ref disposed) == 0 &&
        relay.IsBackendStopReservationCurrent(
            ownedEpoch,
            reservationToken,
            backendPort);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            relay.ReleaseBackendStopReservation(reservationToken);
        }
    }
}

internal sealed class LoopbackRelay : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly TcpListener listener;
    private readonly LoopbackRelayOptions options;
    private readonly Action<Exception> reportError;
    private readonly Func<int, TcpClient?, bool> backendAdmission;
    private readonly CancellationTokenSource shutdown = new();
    private readonly HashSet<RelayConnection> connections = [];
    private readonly Task acceptLoop;
    private readonly int publicPort;
    private int backendPort;
    private long gateEpoch;
    private long? exclusiveGateEpoch;
    private long nextBackendStopReservationToken;
    private long? activeBackendStopReservationToken;
    private bool gated;
    private bool disposed;

    private LoopbackRelay(
        int publicPort,
        int backendPort,
        bool startGated,
        LoopbackRelayOptions options,
        Action<Exception> reportError,
        Func<int, TcpClient?, bool> backendAdmission)
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
        this.backendAdmission = backendAdmission;
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
        Action<Exception>? reportError = null,
        Func<int, TcpClient?, bool>? backendAdmission = null) =>
        new(
            publicPort,
            backendPort,
            startGated,
            options ?? new LoopbackRelayOptions(),
            reportError ?? (_ => { }),
            backendAdmission ?? ((_, _) => true));

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
            gateEpoch++;
            exclusiveGateEpoch = null;
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

    internal async Task<RelayGateLease> CloseGateExclusivelyAsync()
    {
        RelayConnection[] snapshot;
        long ownedEpoch;
        lock (sync)
        {
            ThrowIfDisposed();
            if (gated)
            {
                throw new InvalidOperationException(
                    "The relay gate is already owned by another safety boundary.");
            }
            gated = true;
            ownedEpoch = ++gateEpoch;
            exclusiveGateEpoch = ownedEpoch;
            snapshot = [.. connections];
        }

        foreach (var connection in snapshot)
        {
            connection.Abort();
        }
        if (snapshot.Length != 0)
        {
            await Task.WhenAll(snapshot.Select(connection => connection.Completion)).WaitAsync(
                options.EffectiveGateDrainTimeout);
        }
        return new RelayGateLease(this, ownedEpoch);
    }

    internal bool TryOpenGate(long ownedEpoch)
    {
        lock (sync)
        {
            if (disposed ||
                !gated ||
                gateEpoch != ownedEpoch ||
                exclusiveGateEpoch != ownedEpoch ||
                activeBackendStopReservationToken is not null)
            {
                return false;
            }
            if (connections.Count != 0)
            {
                throw new InvalidOperationException(
                    "The relay gate cannot open until old connections have drained.");
            }
            gated = false;
            exclusiveGateEpoch = null;
            gateEpoch++;
            return true;
        }
    }

    internal RelayBackendStopReservation? TryReserveBackendStop(long ownedEpoch)
    {
        lock (sync)
        {
            if (disposed ||
                !gated ||
                gateEpoch != ownedEpoch ||
                exclusiveGateEpoch != ownedEpoch ||
                activeBackendStopReservationToken is not null)
            {
                return null;
            }
            if (connections.Count != 0)
            {
                throw new InvalidOperationException(
                    "The relay backend cannot stop until old connections have drained.");
            }
            var reservationToken = checked(++nextBackendStopReservationToken);
            activeBackendStopReservationToken = reservationToken;
            return new RelayBackendStopReservation(
                this,
                ownedEpoch,
                reservationToken,
                backendPort);
        }
    }

    internal bool IsBackendStopReservationCurrent(
        long ownedEpoch,
        long reservationToken,
        int ownedBackendPort)
    {
        lock (sync)
        {
            return !disposed &&
                gated &&
                gateEpoch == ownedEpoch &&
                exclusiveGateEpoch == ownedEpoch &&
                activeBackendStopReservationToken == reservationToken &&
                backendPort == ownedBackendPort;
        }
    }

    internal void ReleaseBackendStopReservation(long reservationToken)
    {
        lock (sync)
        {
            if (activeBackendStopReservationToken == reservationToken)
            {
                activeBackendStopReservationToken = null;
            }
        }
    }

    internal bool TryRetargetAndOpenGate(long ownedEpoch, int port)
    {
        LoopbackEndpoint.ValidatePort(port);
        lock (sync)
        {
            if (disposed ||
                !gated ||
                gateEpoch != ownedEpoch ||
                exclusiveGateEpoch != ownedEpoch ||
                activeBackendStopReservationToken is not null)
            {
                return false;
            }
            if (port == publicPort)
            {
                throw new ArgumentException(
                    "Relay and backend ports must be different.",
                    nameof(port));
            }
            if (connections.Count != 0)
            {
                throw new InvalidOperationException(
                    "The relay gate cannot retarget until old connections have drained.");
            }
            backendPort = port;
            gated = false;
            exclusiveGateEpoch = null;
            gateEpoch++;
            return true;
        }
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
            if (exclusiveGateEpoch is not null)
            {
                throw new InvalidOperationException(
                    "The relay backend is owned by an exclusive gate transition.");
            }
            if (activeBackendStopReservationToken is not null)
            {
                throw new InvalidOperationException(
                    "The relay backend is reserved for a stop transition.");
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
            if (exclusiveGateEpoch is not null)
            {
                throw new InvalidOperationException(
                    "The relay gate is owned by an exclusive gate transition.");
            }
            if (activeBackendStopReservationToken is not null)
            {
                throw new InvalidOperationException(
                    "The relay gate is reserved for a backend stop transition.");
            }
            gated = false;
            gateEpoch++;
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

            int candidatePort;
            lock (sync)
            {
                candidatePort = backendPort;
            }
            var admitted = IsBackendAdmissionAllowed(candidatePort, connectedBackend: null);

            RelayConnection? connection = null;
            lock (sync)
            {
                if (admitted &&
                    candidatePort == backendPort &&
                    !disposed &&
                    !gated &&
                    connections.Count < options.MaximumConnections)
                {
                    connection = new RelayConnection(
                        client,
                        candidatePort,
                        options,
                        IsBackendAdmissionAllowed,
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

    private bool IsBackendAdmissionAllowed(int port, TcpClient? connectedBackend)
    {
        try
        {
            return backendAdmission(port, connectedBackend);
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return false;
        }
    }

    private void ReportError(Exception error)
    {
        try
        {
            reportError(error);
        }
        catch
        {
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
            ReportError(error);
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
        Func<int, TcpClient?, bool> backendAdmission,
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
                if (!backendAdmission(backendPort, backend))
                {
                    return;
                }

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
