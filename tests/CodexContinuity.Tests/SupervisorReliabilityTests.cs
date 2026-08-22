using CodexContinuity;
using System.Reflection;
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

    [Fact]
    public async Task UpdateLifetimeUnsubscribesAndAwaitsCancellation()
    {
        ConsoleCancelEventHandler? subscribedHandler = null;
        ConsoleCancelEventHandler? removedHandler = null;
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowUpdaterExit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RunUpdates(
            string _stateDirectory,
            string _runningVersion,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.SetResult();
            }
            await allowUpdaterExit.Task;
        }

        var lifetime = new SupervisorUpdateLifetime(
            root,
            "0.2.1",
            RunUpdates,
            handler => subscribedHandler = handler,
            handler => removedHandler = handler);
        var eventArgs = CreateCancelEventArgs();
        subscribedHandler!(null, eventArgs);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(eventArgs.Cancel);
        Assert.True(lifetime.Token.IsCancellationRequested);
        var disposeTask = lifetime.DisposeAsync().AsTask();
        Assert.False(disposeTask.IsCompleted);
        allowUpdaterExit.SetResult();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(subscribedHandler, removedHandler);
    }

    [Fact]
    public void UpdateLifetimeUnsubscribesWhenRunnerThrowsSynchronously()
    {
        ConsoleCancelEventHandler? subscribedHandler = null;
        ConsoleCancelEventHandler? removedHandler = null;
        CancellationToken runnerToken = default;

        Assert.Throws<InvalidOperationException>(() => new SupervisorUpdateLifetime(
            root,
            "0.2.1",
            (_, _, cancellationToken) =>
            {
                runnerToken = cancellationToken;
                throw new InvalidOperationException("fixture");
            },
            handler => subscribedHandler = handler,
            handler => removedHandler = handler));

        Assert.Same(subscribedHandler, removedHandler);
        Assert.Throws<ObjectDisposedException>(() => runnerToken.WaitHandle);
    }

    [Fact]
    public async Task UpdateLifetimeDisposesAfterUpdaterFault()
    {
        ConsoleCancelEventHandler? subscribedHandler = null;
        ConsoleCancelEventHandler? removedHandler = null;
        var cancellationObserved = false;
        CancellationToken runnerToken = default;
        var lifetime = new SupervisorUpdateLifetime(
            root,
            "0.2.1",
            (_, _, cancellationToken) =>
            {
                runnerToken = cancellationToken;
                cancellationToken.Register(() => cancellationObserved = true);
                return Task.FromException(new InvalidOperationException("fixture"));
            },
            handler => subscribedHandler = handler,
            handler => removedHandler = handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifetime.DisposeAsync().AsTask());

        Assert.Equal("fixture", exception.Message);
        Assert.True(cancellationObserved);
        Assert.Same(subscribedHandler, removedHandler);
        Assert.Throws<ObjectDisposedException>(() => runnerToken.WaitHandle);
    }

    [Fact]
    public async Task UpdateLifetimeStillDisposesWhenCancellationCallbackThrows()
    {
        CancellationToken runnerToken = default;
        var allowUpdaterExit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new SupervisorUpdateLifetime(
            root,
            "0.2.1",
            (_, _, cancellationToken) =>
            {
                runnerToken = cancellationToken;
                cancellationToken.Register(() => throw new InvalidOperationException("fixture"));
                return allowUpdaterExit.Task;
            },
            _ => { },
            _ => { });

        var disposeTask = lifetime.DisposeAsync().AsTask();
        Assert.False(disposeTask.IsCompleted);
        allowUpdaterExit.SetResult();
        await Assert.ThrowsAsync<AggregateException>(() => disposeTask);

        Assert.Throws<ObjectDisposedException>(() => runnerToken.WaitHandle);
    }

    [Fact]
    public async Task RestartBackoffReturnsCleanlyWhenShutdownIsCanceled()
    {
        using var shutdown = new CancellationTokenSource();
        var waitTask = Program.WaitForRestartAsync(
            TimeSpan.FromMinutes(1),
            shutdown.Token);

        Assert.False(waitTask.IsCompleted);
        shutdown.Cancel();

        Assert.False(await waitTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static ConsoleCancelEventArgs CreateCancelEventArgs() =>
        (ConsoleCancelEventArgs)(Activator.CreateInstance(
            typeof(ConsoleCancelEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [ConsoleSpecialKey.ControlC],
            culture: null)
        ?? throw new InvalidOperationException("Could not create console cancel event args."));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
