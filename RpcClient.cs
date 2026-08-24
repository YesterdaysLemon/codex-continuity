using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexContinuity;

internal sealed record ThreadSummary(
    string Id,
    string? Name,
    string Status,
    ThreadLifecycleStatus Activity);

internal sealed class RpcClient : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private TcpClient? transport;
    private HttpMessageInvoker? transportInvoker;
    private int? expectedBackendProcessId;
    private Func<TcpClient, int, bool>? isConnectionAcceptedBy;
    private long nextId;

    private RpcClient()
    {
    }

    public static async Task<RpcClient> ConnectAsync(
        string url,
        int maximumResponseBytes = RpcReadBudget.DefaultMaximumMessageBytes,
        CancellationToken cancellationToken = default) =>
        await ConnectCoreAsync(
            url,
            maximumResponseBytes,
            cancellationToken,
            expectedBackendProcessId: null,
            isConnectionAcceptedBy: null);

    internal static async Task<RpcClient> ConnectOwnedAsync(
        string url,
        int expectedBackendProcessId,
        CancellationToken cancellationToken,
        Func<TcpClient, int, bool>? isConnectionAcceptedBy = null)
    {
        if (expectedBackendProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedBackendProcessId));
        }

        return await ConnectCoreAsync(
            url,
            RpcReadBudget.DefaultMaximumMessageBytes,
            cancellationToken,
            expectedBackendProcessId,
            isConnectionAcceptedBy ?? WindowsTcpPortOwnership.IsLoopbackConnectionAcceptedBy);
    }

    private static async Task<RpcClient> ConnectCoreAsync(
        string url,
        int maximumResponseBytes,
        CancellationToken cancellationToken,
        int? expectedBackendProcessId,
        Func<TcpClient, int, bool>? isConnectionAcceptedBy)
    {
        var client = new RpcClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            if (expectedBackendProcessId is null)
            {
                await client.socket.ConnectAsync(new Uri(url), timeout.Token);
            }
            else
            {
                var handler = new SocketsHttpHandler
                {
                    ConnectCallback = async (context, connectCancellationToken) =>
                    {
                        var connection = new TcpClient(AddressFamily.InterNetwork)
                        {
                            NoDelay = true,
                        };
                        try
                        {
                            await connection.ConnectAsync(
                                context.DnsEndPoint.Host,
                                context.DnsEndPoint.Port,
                                connectCancellationToken);
                            if (Interlocked.CompareExchange(
                                    ref client.transport,
                                    connection,
                                    comparand: null) is not null)
                            {
                                throw new InvalidOperationException(
                                    "Owned RPC connection attempted multiple transports.");
                            }
                            return connection.GetStream();
                        }
                        catch
                        {
                            if (!ReferenceEquals(client.transport, connection))
                            {
                                connection.Dispose();
                            }
                            throw;
                        }
                    },
                };
                client.transportInvoker = new HttpMessageInvoker(
                    handler,
                    disposeHandler: true);
                client.expectedBackendProcessId = expectedBackendProcessId;
                client.isConnectionAcceptedBy = isConnectionAcceptedBy;
                await client.socket.ConnectAsync(
                    new Uri(url),
                    client.transportInvoker,
                    timeout.Token);
                client.VerifyOwnedConnection();
            }
            var initialize = await client.RequestAsync(
                "initialize",
                new JsonObject
                {
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "codex_continuity",
                        ["title"] = "Codex Continuity",
                        ["version"] = ProductVersion(),
                    },
                    ["capabilities"] = new JsonObject(),
                },
                timeout.Token,
                maximumResponseBytes);
            if (initialize["error"] is not null)
            {
                throw new InvalidOperationException(
                    $"App-server initialization failed: {initialize["error"]}");
            }
            await client.SendAsync(new JsonObject
            {
                ["method"] = "initialized",
                ["params"] = new JsonObject(),
            }, timeout.Token);
            client.VerifyOwnedConnectionIfRequired();
            return client;
        }
        catch
        {
            client.socket.Dispose();
            client.transportInvoker?.Dispose();
            client.transport?.Dispose();
            throw;
        }
    }

    public Task<List<ThreadSummary>> ListThreadsAsync(
        int maximumThreads = RpcReadBudget.DefaultMaximumItems,
        int maximumPages = RpcReadBudget.DefaultMaximumPages,
        int maximumMessageBytes = RpcReadBudget.DefaultMaximumMessageBytes,
        TimeSpan? operationTimeout = null,
        CancellationToken cancellationToken = default) =>
        ListThreadPagesAsync(
            ParseThreadData,
            maximumThreads,
            maximumPages,
            maximumMessageBytes,
            operationTimeout,
            cancellationToken);

    public Task<List<ThreadLifecycleStatus>> ListThreadLifecyclesAsync(
        int maximumThreads = RpcReadBudget.DefaultMaximumItems,
        int maximumPages = RpcReadBudget.DefaultMaximumPages,
        int maximumMessageBytes = RpcReadBudget.DefaultMaximumMessageBytes,
        TimeSpan? operationTimeout = null,
        CancellationToken cancellationToken = default) =>
        ListThreadPagesAsync(
            ParseThreadLifecycleData,
            maximumThreads,
            maximumPages,
            maximumMessageBytes,
            operationTimeout,
            cancellationToken);

    internal Task<List<string>> ListThreadIdsAsync(
        int maximumThreads = RpcReadBudget.DefaultMaximumItems,
        int maximumPages = RpcReadBudget.DefaultMaximumPages,
        int maximumMessageBytes = RpcReadBudget.DefaultMaximumMessageBytes,
        TimeSpan? operationTimeout = null,
        CancellationToken cancellationToken = default) =>
        ListThreadPagesAsync(
            ParseThreadIdData,
            maximumThreads,
            maximumPages,
            maximumMessageBytes,
            operationTimeout,
            cancellationToken);

    internal async Task<List<string>> ListOwnedThreadIdsAsync(
        CancellationToken cancellationToken)
    {
        VerifyOwnedConnection();
        var threadIds = await ListThreadPagesAsync(
            ParseThreadIdData,
            RpcReadBudget.DefaultMaximumItems,
            RpcReadBudget.DefaultMaximumPages,
            RpcReadBudget.DefaultMaximumMessageBytes,
            RpcReadBudget.DefaultOperationTimeout,
            cancellationToken);
        VerifyOwnedConnection();
        return threadIds;
    }

    internal async Task<List<ThreadLifecycleStatus>> ListOwnedThreadLifecyclesAsync(
        CancellationToken cancellationToken)
    {
        VerifyOwnedConnection();
        var lifecycles = await ListThreadLifecyclesAsync(
            cancellationToken: cancellationToken);
        VerifyOwnedConnection();
        return lifecycles;
    }

    private async Task<List<T>> ListThreadPagesAsync<T>(
        Func<JsonNode?, IReadOnlyList<T>> parseData,
        int maximumThreads,
        int maximumPages,
        int maximumMessageBytes,
        TimeSpan? operationTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumMessageBytes, 1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(operationTimeout ?? RpcReadBudget.DefaultOperationTimeout);
        var budget = new RpcReadBudget(maximumThreads, maximumPages);
        var threads = new List<T>();
        string? cursor = null;
        do
        {
            budget.BeginPage();
            var parameters = new JsonObject { ["limit"] = 100 };
            if (cursor is not null)
            {
                parameters["cursor"] = cursor;
            }
            var response = await RequestAsync(
                "thread/list",
                parameters,
                timeout.Token,
                maximumMessageBytes);
            ThrowIfRpcError(response, "thread/list");
            var result = response["result"]?.AsObject()
                ?? throw new InvalidOperationException("thread/list returned no result.");
            var page = parseData(result["data"]);
            budget.AddItems(page.Count);
            threads.AddRange(page);
            cursor = result["nextCursor"]?.GetValue<string>();
            budget.ObserveCursor(cursor);
        }
        while (cursor is not null);
        return threads;
    }

    internal static IReadOnlyList<ThreadLifecycleStatus> ParseThreadLifecycleData(
        JsonNode? dataNode)
    {
        if (dataNode is not JsonArray data)
        {
            throw new InvalidOperationException("thread/list returned no data array.");
        }

        var threads = new List<ThreadLifecycleStatus>(data.Count);
        foreach (var node in data)
        {
            if (node is not JsonObject thread)
            {
                throw new InvalidOperationException(
                    "thread/list returned a malformed thread entry.");
            }
            threads.Add(ThreadLifecycleStatus.Parse(thread["status"]));
        }
        return threads;
    }

    internal static IReadOnlyList<string> ParseThreadIdData(JsonNode? dataNode)
    {
        if (dataNode is not JsonArray data)
        {
            throw new InvalidOperationException("thread/list returned no data array.");
        }

        var threadIds = new List<string>(data.Count);
        foreach (var node in data)
        {
            if (node is not JsonObject thread ||
                thread["id"] is not JsonValue idValue ||
                !idValue.TryGetValue<string>(out var id) ||
                string.IsNullOrWhiteSpace(id) ||
                id.Length > SupervisorSuccessorHandoff.MaximumThreadIdCharacters ||
                id.Any(char.IsControl))
            {
                throw new InvalidOperationException(
                    "thread/list returned a malformed thread identity.");
            }
            threadIds.Add(id);
        }
        if (threadIds.Distinct(StringComparer.Ordinal).Count() != threadIds.Count)
        {
            throw new InvalidOperationException(
                "thread/list returned duplicate thread identities.");
        }
        return threadIds;
    }

    internal static IReadOnlyList<ThreadSummary> ParseThreadData(JsonNode? dataNode)
    {
        if (dataNode is not JsonArray data)
        {
            throw new InvalidOperationException("thread/list returned no data array.");
        }

        var threads = new List<ThreadSummary>(data.Count);
        foreach (var node in data)
        {
            if (node is not JsonObject thread)
            {
                throw new InvalidOperationException(
                    "thread/list returned a malformed thread entry.");
            }
            var activity = ThreadLifecycleStatus.Parse(thread["status"]);
            threads.Add(new ThreadSummary(
                thread["id"]?.GetValue<string>() ?? string.Empty,
                thread["name"]?.GetValue<string>(),
                activity.Type,
                activity));
        }
        return threads;
    }

    public async Task<JsonObject> RequestAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken = default,
        int maximumResponseBytes = RpcReadBudget.DefaultMaximumMessageBytes)
    {
        var id = Interlocked.Increment(ref nextId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await SendAsync(new JsonObject
        {
            ["method"] = method,
            ["id"] = id,
            ["params"] = parameters,
        }, timeout.Token);

        while (true)
        {
            var message = await ReceiveAsync(timeout.Token, maximumResponseBytes);
            if (message["id"]?.GetValue<long>() == id)
            {
                return message;
            }
        }
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private async Task<JsonObject> ReceiveAsync(
        CancellationToken cancellationToken,
        int maximumResponseBytes)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("App-server closed the WebSocket connection.");
            }
            RpcReadBudget.EnsureMessageFits(
                stream.Length,
                result.Count,
                maximumResponseBytes);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return JsonNode.Parse(Encoding.UTF8.GetString(stream.ToArray()))?.AsObject()
            ?? throw new InvalidOperationException("App-server returned invalid JSON.");
    }

    private static void ThrowIfRpcError(JsonObject response, string method)
    {
        if (response["error"] is not null)
        {
            throw new InvalidOperationException($"{method} failed: {response["error"]}");
        }
    }

    private void VerifyOwnedConnectionIfRequired()
    {
        if (expectedBackendProcessId is not null)
        {
            VerifyOwnedConnection();
        }
    }

    private void VerifyOwnedConnection()
    {
        if (transport is null ||
            expectedBackendProcessId is not { } processId ||
            isConnectionAcceptedBy is null ||
            !isConnectionAcceptedBy(transport, processId))
        {
            throw new InvalidOperationException(
                "Private RPC connection is not owned by the expected backend process.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "done",
                    timeout.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }
        }
        socket.Dispose();
        transportInvoker?.Dispose();
        transport?.Dispose();
    }

    private static string ProductVersion()
    {
        var version = typeof(RpcClient).Assembly.GetName().Version;
        return version is null
            ? "development"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
