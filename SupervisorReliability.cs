using System.Text;
using System.Text.Json;

namespace CodexContinuity;

internal sealed class SupervisorUpdateLifetime : IAsyncDisposable
{
    private readonly CancellationTokenSource shutdown = new();
    private readonly Action<ConsoleCancelEventHandler> unsubscribe;
    private readonly ConsoleCancelEventHandler cancelHandler;
    private readonly Task updateTask;
    private int disposed;

    internal SupervisorUpdateLifetime(
        string stateDirectory,
        string runningVersion,
        Func<string, string, CancellationToken, Task> runUpdates)
        : this(
            stateDirectory,
            runningVersion,
            runUpdates,
            handler => Console.CancelKeyPress += handler,
            handler => Console.CancelKeyPress -= handler)
    {
    }

    internal SupervisorUpdateLifetime(
        string stateDirectory,
        string runningVersion,
        Func<string, string, CancellationToken, Task> runUpdates,
        Action<ConsoleCancelEventHandler> subscribe,
        Action<ConsoleCancelEventHandler> unsubscribe)
    {
        this.unsubscribe = unsubscribe;
        cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        var subscribed = false;
        try
        {
            subscribe(cancelHandler);
            subscribed = true;
            updateTask = runUpdates(stateDirectory, runningVersion, shutdown.Token);
        }
        catch
        {
            try
            {
                if (subscribed)
                {
                    unsubscribe(cancelHandler);
                }
            }
            finally
            {
                shutdown.Dispose();
            }
            throw;
        }
    }

    internal CancellationToken Token => shutdown.Token;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            unsubscribe(cancelHandler);
        }
        finally
        {
            try
            {
                shutdown.Cancel();
            }
            finally
            {
                try
                {
                    await updateTask;
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                }
                finally
                {
                    shutdown.Dispose();
                }
            }
        }
    }
}

internal sealed class RestartBackoffPolicy(
    TimeSpan? initialDelay = null,
    TimeSpan? maximumDelay = null,
    double jitterFraction = 0.2)
{
    private readonly TimeSpan initialDelay = initialDelay ?? TimeSpan.FromSeconds(2);
    private readonly TimeSpan maximumDelay = maximumDelay ?? TimeSpan.FromMinutes(1);

    internal TimeSpan DelayForFailure(int consecutiveFailures, double jitterSample)
    {
        if (consecutiveFailures < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(consecutiveFailures),
                "Failure count must be positive.");
        }
        if (jitterSample is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jitterSample),
                "Jitter sample must be between zero and one.");
        }

        var exponent = Math.Min(consecutiveFailures - 1, 30);
        var baseMilliseconds = Math.Min(
            initialDelay.TotalMilliseconds * Math.Pow(2, exponent),
            maximumDelay.TotalMilliseconds);
        var jitterMultiplier = 1 - jitterFraction + (2 * jitterFraction * jitterSample);
        return TimeSpan.FromMilliseconds(Math.Min(
            baseMilliseconds * jitterMultiplier,
            maximumDelay.TotalMilliseconds));
    }
}

internal sealed class RollingLogWriter(
    string path,
    long maximumBytes = 5 * 1024 * 1024,
    int retainedFiles = 3)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    internal async Task AppendLineAsync(string line, CancellationToken cancellationToken)
    {
        var entry = CreateBoundedEntry(line);
        var entryBytes = Encoding.UTF8.GetByteCount(entry);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException($"Log path has no directory: {path}");
            Directory.CreateDirectory(directory);
            if (File.Exists(path) && new FileInfo(path).Length + entryBytes > maximumBytes)
            {
                Rotate();
            }
            await File.AppendAllTextAsync(path, entry, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private string CreateBoundedEntry(string line)
    {
        var entryBudget = maximumBytes - Encoding.UTF8.GetPreamble().Length;
        var prefix = $"{DateTimeOffset.UtcNow:O} ";
        var fullEntry = $"{prefix}{line}{Environment.NewLine}";
        if (Encoding.UTF8.GetByteCount(fullEntry) <= entryBudget)
        {
            return fullEntry;
        }

        const string truncationMarker = "… [truncated]";
        var suffix = $"{truncationMarker}{Environment.NewLine}";
        var availableBytes = entryBudget -
            Encoding.UTF8.GetByteCount(prefix) -
            Encoding.UTF8.GetByteCount(suffix);
        if (availableBytes <= 0 || availableBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytes),
                "Maximum log size must leave room for a timestamp and truncation marker.");
        }

        var buffer = new byte[(int)availableBytes];
        Encoding.UTF8.GetEncoder().Convert(
            line.AsSpan(),
            buffer.AsSpan(),
            flush: true,
            out _,
            out var bytesUsed,
            out _);
        return $"{prefix}{Encoding.UTF8.GetString(buffer, 0, bytesUsed)}{suffix}";
    }

    private void Rotate()
    {
        if (retainedFiles <= 0)
        {
            File.Delete(path);
            return;
        }

        var oldest = RotatedPath(retainedFiles);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }
        for (var index = retainedFiles - 1; index >= 1; index--)
        {
            var source = RotatedPath(index);
            if (File.Exists(source))
            {
                File.Move(source, RotatedPath(index + 1), overwrite: true);
            }
        }
        File.Move(path, RotatedPath(1), overwrite: true);
    }

    private string RotatedPath(int index) => $"{path}.{index}";
}

internal sealed record SupervisorStatus(
    string State,
    int SupervisorProcessId,
    int? BackendProcessId,
    int Port,
    string? CodexHome,
    int ConsecutiveFailures,
    int? LastExitCode,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? NextRetryAtUtc,
    string? Detail);

internal sealed class SupervisorStatusStore(string path)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal SupervisorStatus? Read()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<SupervisorStatus>(
                    File.ReadAllText(path),
                    SerializerOptions)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal void Write(SupervisorStatus status)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Status path has no directory: {path}");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(status, SerializerOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
