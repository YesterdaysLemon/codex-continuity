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
        Assert.Throws<InvalidOperationException>(() => Program.RpcClient.ParseThreadData(null));
        Assert.Throws<InvalidOperationException>(() => Program.RpcClient.ParseThreadData(
            JsonNode.Parse("""[null]""")));

        var malformedStatus = Assert.Single(Program.RpcClient.ParseThreadData(JsonNode.Parse(
            """[{"id":"thread-1","status":{"type":12}}]""")));
        Assert.Equal("unknown", malformedStatus.Status);

        Assert.Equal(
            [new Program.ThreadSummary(
                "thread-2",
                "Fixture",
                "idle",
                new ThreadLifecycleStatus("idle", [], Malformed: false))],
            Program.RpcClient.ParseThreadData(JsonNode.Parse(
                """[{"id":"thread-2","name":"Fixture","status":{"type":"idle"}}]""")));
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
                    """[{"id":"thread-1","name":"First","status":{"type":"active"}}]"""),
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

        await using var client = await Program.RpcClient.ConnectAsync(url);
        Assert.Equal(
            [
                new Program.ThreadSummary("thread-1", "First", "active"),
                new Program.ThreadSummary("thread-2", "Second", "idle"),
            ],
            await client.ListThreadsAsync());
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
            Program.RpcClient.ConnectAsync(oversizedUrl, maximumResponseBytes: 128));
        await oversizedServer;

        var (delayedUrl, delayedServer) = StartServer(async socket =>
        {
            await CompleteInitializationAsync(socket);
            _ = await ReceiveAsync(socket);
            await Task.Delay(250);
        });
        await using var client = await Program.RpcClient.ConnectAsync(delayedUrl);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListThreadsAsync(
            operationTimeout: TimeSpan.FromMilliseconds(50)));
        await delayedServer;
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
