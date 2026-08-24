using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class RpcReadBudgetTests
{
    [Fact]
    public void ThreadPageParserRejectsMissingOrDisappearingEntries()
    {
        Assert.Throws<InvalidOperationException>(() => RpcClient.ParseThreadData(null));
        Assert.Throws<InvalidOperationException>(() => RpcClient.ParseThreadData(
            JsonNode.Parse("""[null]""")));

        var malformedStatus = Assert.Single(RpcClient.ParseThreadData(JsonNode.Parse(
            """[{"id":"thread-1","status":{"type":12}}]""")));
        Assert.Equal("unknown", malformedStatus.Status);
        Assert.True(malformedStatus.Activity.Malformed);

        Assert.Equal(
            [new ThreadSummary(
                "thread-2",
                "Fixture",
                "idle",
                new ThreadLifecycleStatus("idle", [], Malformed: false))],
            RpcClient.ParseThreadData(JsonNode.Parse(
                """[{"id":"thread-2","name":"Fixture","status":{"type":"idle"}}]""")));

        Assert.Equal(
            [new ThreadLifecycleStatus("idle", [], Malformed: false)],
            RpcClient.ParseThreadLifecycleData(JsonNode.Parse(
                """[{"id":{"ignored":true},"name":["ignored"],"status":{"type":"idle"}}]""")));
        Assert.Equal(
            ["thread-1", "thread-2"],
            RpcClient.ParseThreadIdData(JsonNode.Parse(
                """[{"id":"thread-1","name":"ignored"},{"id":"thread-2"}]""")));
        Assert.Throws<InvalidOperationException>(() => RpcClient.ParseThreadIdData(
            JsonNode.Parse("""[{"id":"thread-1"},{"id":"thread-1"}]""")));
        Assert.Throws<InvalidOperationException>(() => RpcClient.ParseThreadIdData(
            JsonNode.Parse("""[{"id":""}]""")));
    }

    [Fact]
    public void PageItemAndCursorBudgetsFailClosed()
    {
        var budget = new RpcReadBudget(maximumItems: 2, maximumPages: 2);
        budget.BeginPage();
        budget.AddItems(2);
        budget.ObserveCursor("next");
        budget.BeginPage();

        Assert.Throws<InvalidOperationException>(() => budget.AddItems(1));
        Assert.Throws<InvalidOperationException>(() => budget.ObserveCursor(" "));
        Assert.Throws<InvalidOperationException>(() => budget.ObserveCursor("next"));
        Assert.Throws<InvalidOperationException>(budget.BeginPage);
    }

    [Theory]
    [InlineData(0L, 4, 4)]
    [InlineData(3L, 1, 4)]
    public void MessageBudgetAcceptsItsBoundary(long current, int appended, int maximum) =>
        RpcReadBudget.EnsureMessageFits(current, appended, maximum);

    [Fact]
    public void MessageBudgetRejectsOversizedInput()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RpcReadBudget.EnsureMessageFits(currentBytes: 4, appendedBytes: 1, maximumBytes: 4));
    }

    [Fact]
    public async Task RpcClientPreservesValidMultiPageResults()
    {
        var (url, server) = StartServer(async socket =>
        {
            await CompleteInitializationAsync(socket);
            var first = await ReceiveAsync(socket);
            await RespondAsync(socket, first, new JsonObject
            {
                ["data"] = JsonNode.Parse(
                    """[{"id":"thread-1","name":"First","status":{"type":"active","activeFlags":[]}}]"""),
                ["nextCursor"] = "second-page",
            });
            var second = await ReceiveAsync(socket);
            Assert.Equal(
                "second-page",
                second["params"]?["cursor"]?.GetValue<string>());
            await RespondAsync(socket, second, new JsonObject
            {
                ["data"] = JsonNode.Parse(
                    """[{"id":"thread-2","name":"Second","status":{"type":"idle"}}]"""),
                ["nextCursor"] = null,
            });
        });

        await using var client = await RpcClient.ConnectAsync(url);
        var threads = await client.ListThreadsAsync();
        Assert.Equal(
            [("thread-1", "First", "active"), ("thread-2", "Second", "idle")],
            threads.Select(thread => (thread.Id, thread.Name, thread.Status)));
        Assert.All(threads, thread => Assert.False(thread.Activity.Malformed));
        await server;

        var (lifecycleUrl, lifecycleServer) = StartServer(async socket =>
        {
            await CompleteInitializationAsync(socket);
            var first = await ReceiveAsync(socket);
            await RespondAsync(socket, first, new JsonObject
            {
                ["data"] = JsonNode.Parse(
                    """[{"id":{"ignored":true},"name":["ignored"],"status":{"type":"active","activeFlags":[]}}]"""),
                ["nextCursor"] = "lifecycle-page-2",
            });
            var second = await ReceiveAsync(socket);
            Assert.Equal(
                "lifecycle-page-2",
                second["params"]?["cursor"]?.GetValue<string>());
            await RespondAsync(socket, second, new JsonObject
            {
                ["data"] = JsonNode.Parse(
                    """[{"id":null,"name":{"ignored":true},"status":{"type":"idle"}}]"""),
                ["nextCursor"] = null,
            });
        });
        var connectionChecks = 0;
        await using var lifecycleClient = await RpcClient.ConnectOwnedAsync(
            lifecycleUrl,
            expectedBackendProcessId: 42,
            CancellationToken.None,
            (_, processId) =>
            {
                connectionChecks++;
                return processId == 42;
            });
        var lifecycles = await lifecycleClient.ListOwnedThreadLifecyclesAsync(
            CancellationToken.None);
        Assert.Equivalent(
            new ThreadLifecycleStatus[]
            {
                new ThreadLifecycleStatus("active", [], Malformed: false),
                new ThreadLifecycleStatus("idle", [], Malformed: false),
            },
            lifecycles,
            strict: true);
        Assert.True(connectionChecks >= 4);
        await lifecycleServer;
    }

    [Fact]
    public async Task OwnedRpcRejectsAForeignConnectedSessionBeforeInitialization()
    {
        var (url, server) = StartServer(_ => Task.Delay(250));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RpcClient.ConnectOwnedAsync(
                url,
                expectedBackendProcessId: 42,
                CancellationToken.None,
                (_, _) => false));

        Assert.Contains("not owned", error.Message, StringComparison.OrdinalIgnoreCase);
        await server;
    }

    [Fact]
    public async Task OwnedThreadIdentityReadIsPagedAndNeverParsesTitles()
    {
        var (url, server) = StartServer(async socket =>
        {
            await CompleteInitializationAsync(socket);
            var first = await ReceiveAsync(socket);
            await RespondAsync(socket, first, new JsonObject
            {
                ["data"] = JsonNode.Parse(
                    """[{"id":"thread-1","name":{"ignored":true}}]"""),
                ["nextCursor"] = "next",
            });
            var second = await ReceiveAsync(socket);
            Assert.Equal("next", second["params"]?["cursor"]?.GetValue<string>());
            await RespondAsync(socket, second, new JsonObject
            {
                ["data"] = JsonNode.Parse(
                    """[{"id":"thread-2","name":["ignored"]}]"""),
                ["nextCursor"] = null,
            });
        });
        var connectionChecks = 0;
        await using var client = await RpcClient.ConnectOwnedAsync(
            url,
            expectedBackendProcessId: 42,
            CancellationToken.None,
            (_, processId) =>
            {
                connectionChecks++;
                return processId == 42;
            });

        Assert.Equal(
            ["thread-1", "thread-2"],
            await client.ListOwnedThreadIdsAsync(CancellationToken.None));
        Assert.True(connectionChecks >= 4);
        await server;
    }

    [Fact]
    public async Task OwnedRpcRejectsConnectionOwnershipLossAfterLifecycleRead()
    {
        var (url, server) = StartServer(async socket =>
        {
            await CompleteInitializationAsync(socket);
            var list = await ReceiveAsync(socket);
            await RespondAsync(socket, list, new JsonObject
            {
                ["data"] = JsonNode.Parse("""[{"status":{"type":"idle"}}]"""),
                ["nextCursor"] = null,
            });
        });
        var connectionChecks = 0;
        await using var client = await RpcClient.ConnectOwnedAsync(
            url,
            expectedBackendProcessId: 42,
            CancellationToken.None,
            (_, _) => ++connectionChecks < 4);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ListOwnedThreadLifecyclesAsync(CancellationToken.None));

        Assert.Contains("not owned", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, connectionChecks);
        await server;
    }

    [Fact]
    public async Task RpcClientBoundsInitializationAndTheWholeListOperation()
    {
        var (oversizedUrl, oversizedServer) = StartServer(async socket =>
        {
            var initialize = await ReceiveAsync(socket);
            await RespondAsync(socket, initialize, new JsonObject
            {
                ["padding"] = new string('x', 256),
            });
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RpcClient.ConnectAsync(oversizedUrl, maximumResponseBytes: 128));
        await oversizedServer;

        var (delayedUrl, delayedServer) = StartServer(async socket =>
        {
            await CompleteInitializationAsync(socket);
            _ = await ReceiveAsync(socket);
            await Task.Delay(250);
        });
        await using var client = await RpcClient.ConnectAsync(delayedUrl);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListThreadsAsync(
            operationTimeout: TimeSpan.FromMilliseconds(50)));
        await delayedServer;
    }

    [Fact]
    public async Task ReadinessConnectAndListHonorCallerCancellation()
    {
        var readinessPort = AvailablePort();
        using (var listener = new HttpListener())
        {
            listener.Prefixes.Add($"http://127.0.0.1:{readinessPort}/");
            listener.Start();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Program.IsReadyAsync(
                    readinessPort,
                    TimeSpan.FromSeconds(10),
                    cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        }

        var connectPort = AvailablePort();
        using (var listener = new HttpListener())
        {
            listener.Prefixes.Add($"http://127.0.0.1:{connectPort}/");
            listener.Start();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RpcClient.ConnectAsync(
                    $"ws://127.0.0.1:{connectPort}",
                    cancellationToken: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        }

        var (url, server) = StartServer(async socket =>
        {
            await CompleteInitializationAsync(socket);
            _ = await ReceiveAsync(socket);
            await Task.Delay(250);
        });
        await using var client = await RpcClient.ConnectAsync(url);
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.ListThreadLifecyclesAsync(
                    cancellationToken: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        await server;
    }

    private static (string Url, Task Server) StartServer(Func<WebSocket, Task> handle)
    {
        var port = AvailablePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = Task.Run(async () =>
        {
            try
            {
                var context = await listener.GetContextAsync();
                var webSocket = await context.AcceptWebSocketAsync(subProtocol: null);
                using var socket = webSocket.WebSocket;
                await handle(socket);
            }
            finally
            {
                listener.Close();
            }
        });
        return ($"ws://127.0.0.1:{port}", server);
    }

    private static async Task CompleteInitializationAsync(WebSocket socket)
    {
        var initialize = await ReceiveAsync(socket);
        await RespondAsync(socket, initialize, new JsonObject());
        var initialized = await ReceiveAsync(socket);
        Assert.Equal("initialized", initialized["method"]?.GetValue<string>());
    }

    private static async Task<JsonObject> ReceiveAsync(WebSocket socket)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return JsonNode.Parse(Encoding.UTF8.GetString(stream.ToArray()))!.AsObject();
    }

    private static Task RespondAsync(
        WebSocket socket,
        JsonObject request,
        JsonObject result)
    {
        var response = new JsonObject
        {
            ["id"] = request["id"]!.GetValue<long>(),
            ["result"] = result,
        };
        var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
        return socket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
    }

    private static int AvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
