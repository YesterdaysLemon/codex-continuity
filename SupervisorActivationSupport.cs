using System.Net.Http;
using System.Net.WebSockets;

namespace CodexContinuity;

internal static class SupervisorActivationSupport
{
    internal static bool SameOptionalPath(string? left, string? right) =>
        left is null && right is null ||
        left is not null && right is not null && Path.GetFullPath(left).Equals(
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    internal static bool DesktopAnchorStillRunning(
        IReadOnlyList<CodexDesktopProcessIdentity> expected,
        CodexDesktopObservation current) =>
        current.Kind == CodexDesktopObservationKind.Running &&
        expected.All(current.Processes.Contains);

    internal static async Task<IReadOnlyList<string>> ReadOwnedThreadIdsAsync(
        int backendPort,
        int backendProcessId,
        CancellationToken cancellationToken)
    {
        await using var client = await RpcClient.ConnectOwnedAsync(
            LoopbackEndpoint.WebSocketUrl(backendPort),
            backendProcessId,
            cancellationToken);
        return await client.ListOwnedThreadIdsAsync(cancellationToken);
    }

    internal static bool IsExpectedFailure(Exception exception) => exception is
        ArgumentException or IOException or InvalidDataException or InvalidOperationException or
        NotSupportedException or System.Text.Json.JsonException or TimeoutException or
        UnauthorizedAccessException or System.ComponentModel.Win32Exception or
        HttpRequestException or WebSocketException;

    internal static string BoundError(string error)
    {
        const int maximumLength = 2048;
        var singleLine = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= maximumLength
            ? singleLine
            : $"{singleLine[..(maximumLength - 1)]}…";
    }
}
