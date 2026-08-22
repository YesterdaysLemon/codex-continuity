using System.Diagnostics;

namespace CodexContinuity;

internal static class ContinuityLifecycleLock
{
    internal static FileStream Acquire(
        string stateDirectory,
        TimeSpan? timeout = null)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(10);
        if (wait < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Directory.CreateDirectory(stateDirectory);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(
                    ContinuityPaths.LifecycleLockFile(stateDirectory),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException) when (stopwatch.Elapsed < wait)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50));
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException(
                    "Another Continuity install, uninstall, rollback, or automatic update is already in progress.",
                    exception);
            }
        }
    }
}
