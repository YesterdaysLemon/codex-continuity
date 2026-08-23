using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexContinuity.ProcessHarness;

internal static class FakeSelfTestAppServer
{
    private const string WebSocketMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaximumHeaderBytes = 32 * 1024;

    internal static async Task<int> RunAsync(
        int port,
        string stopBehavior,
        string? signalMarkerPath)
    {
        using var shutdown = new CancellationTokenSource();
        var exitCode = stopBehavior switch
        {
            "nonzero" => 17,
            "control-exit" => unchecked((int)0xC000013A),
            _ => 0,
        };
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (signalMarkerPath is not null)
            {
                File.WriteAllText(signalMarkerPath, eventArgs.SpecialKey.ToString());
            }
            if (stopBehavior != "ignore")
            {
                shutdown.Cancel();
            }
        };
        Console.CancelKeyPress += cancelHandler;
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var threadCreated = false;
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(shutdown.Token);
                    threadCreated = await HandleClientAsync(
                        client,
                        threadCreated,
                        shutdown.Token);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (
                    exception is IOException or SocketException or WebSocketException)
                {
                }
            }
            return exitCode;
        }
        finally
        {
            listener.Stop();
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<bool> HandleClientAsync(
        TcpClient client,
        bool threadCreated,
        CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        var headers = await ReadHeadersAsync(stream, cancellationToken);
        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, cancellationToken);
            return threadCreated;
        }

        var acceptBytes = SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketMagic));
        var handshake = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {Convert.ToBase64String(acceptBytes)}\r\n\r\n");
        await stream.WriteAsync(handshake, cancellationToken);
        using var socket = WebSocket.CreateFromStream(
            stream,
            isServer: true,
            subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(30));
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var request = await ReceiveAsync(socket, cancellationToken);
            if (request is null)
            {
                break;
            }
            if (request["id"] is not JsonValue idNode)
            {
                continue;
            }

            var method = request["method"]?.GetValue<string>();
            JsonObject result;
            switch (method)
            {
                case "initialize":
                    result = new JsonObject();
                    break;
                case "thread/start":
                    threadCreated = true;
                    result = new JsonObject
                    {
                        ["thread"] = new JsonObject { ["id"] = "fake-thread" },
                    };
                    break;
                case "thread/loaded/list":
                    result = new JsonObject
                    {
                        ["data"] = threadCreated
                            ? new JsonArray("fake-thread")
                            : new JsonArray(),
                    };
                    break;
                default:
                    result = new JsonObject();
                    break;
            }
            await SendAsync(socket, new JsonObject
            {
                ["id"] = idNode.GetValue<long>(),
                ["result"] = result,
            }, cancellationToken);
        }
        return threadCreated;
    }

    private static async Task<Dictionary<string, string>> ReadHeadersAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var nextByte = new byte[1];
        uint tail = 0;
        while (buffer.Length < MaximumHeaderBytes)
        {
            if (await stream.ReadAsync(nextByte, cancellationToken) == 0)
            {
                throw new IOException("The HTTP request ended before its headers.");
            }
            var value = nextByte[0];
            buffer.WriteByte((byte)value);
            tail = (tail << 8) | value;
            if (tail == 0x0d0a0d0a)
            {
                var lines = Encoding.ASCII.GetString(buffer.ToArray())
                    .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                return lines.Skip(1)
                    .Select(line => line.Split(':', count: 2))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(
                        parts => parts[0].Trim(),
                        parts => parts[1].Trim(),
                        StringComparer.OrdinalIgnoreCase);
            }
        }
        throw new IOException("The HTTP request headers exceeded the test limit.");
    }

    private static async Task<JsonObject?> ReceiveAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return JsonNode.Parse(message.ToArray())?.AsObject();
    }

    private static Task SendAsync(
        WebSocket socket,
        JsonObject message,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        return socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }
}
