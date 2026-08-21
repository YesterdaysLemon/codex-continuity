using System.Net;

namespace CodexContinuity;

internal static class LoopbackEndpoint
{
    internal const int DefaultPort = 45123;

    internal static string WebSocketUrl(int port)
    {
        ValidatePort(port);
        return $"ws://127.0.0.1:{port}";
    }

    internal static string ReadyUrl(int port)
    {
        ValidatePort(port);
        return $"http://127.0.0.1:{port}/readyz";
    }

    internal static void ValidatePort(int port)
    {
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be a valid TCP port.");
        }
    }
}
