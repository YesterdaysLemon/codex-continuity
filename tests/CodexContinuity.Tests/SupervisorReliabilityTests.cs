using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class SupervisorReliabilityTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-supervisor-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(8, 60)]
    public void BackoffIsExponentialAndCapped(int failureCount, double seconds)
    {
        var policy = new RestartBackoffPolicy();

        var delay = policy.DelayForFailure(failureCount, jitterSample: 0.5);

        Assert.Equal(TimeSpan.FromSeconds(seconds), delay);
    }

    [Fact]
    public async Task RollingLogRetainsBoundedHistory()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "app-server.log");
        var writer = new RollingLogWriter(path, maximumBytes: 90, retainedFiles: 2);

        for (var index = 0; index < 12; index++)
        {
            await writer.AppendLineAsync($"entry-{index:D2}-abcdefghijklmnop", CancellationToken.None);
        }

        var files = Directory.GetFiles(root, "app-server.log*");
        Assert.InRange(files.Length, 2, 3);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
        Assert.DoesNotContain(files, file => file.EndsWith(".3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RollingLogTruncatesSingleEntryToMaximumFileSize()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "app-server.log");
        var writer = new RollingLogWriter(path, maximumBytes: 90, retainedFiles: 2);

        await writer.AppendLineAsync(new string('x', 10_000), CancellationToken.None);

        Assert.InRange(new FileInfo(path).Length, 1, 90);
        Assert.Contains("[truncated]", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void EndpointCanOnlyProduceLoopbackUrls()
    {
        var websocket = new Uri(LoopbackEndpoint.WebSocketUrl(45123));
        var readiness = new Uri(LoopbackEndpoint.ReadyUrl(45123));

        Assert.Equal("127.0.0.1", websocket.Host);
        Assert.Equal("127.0.0.1", readiness.Host);
        Assert.True(System.Net.IPAddress.IsLoopback(System.Net.IPAddress.Parse(websocket.Host)));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
