using System.ComponentModel;
using System.Diagnostics;

namespace CodexContinuity;

internal sealed record SupervisorCompatibilityScope(
    IReadOnlyList<string> StateDirectories)
{
    internal static SupervisorCompatibilityScope ForStateDirectory(string stateDirectory) =>
        new([stateDirectory]);

}

internal enum RecordedSupervisorState
{
    Active,
    Stale,
    Unsafe,
}

internal static class SupervisorCompatibilityGuard
{
    internal static void EnsureNoActiveRecord(
        SupervisorCompatibilityScope scope,
        string operation)
    {
        foreach (var stateDirectory in scope.StateDirectories
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var loaded = new SupervisorStatusStore(
                ContinuityPaths.SupervisorStatusFile(stateDirectory)).Load();
            if (loaded.Kind == SupervisorStatusLoadKind.Missing)
            {
                continue;
            }
            if (loaded.Kind == SupervisorStatusLoadKind.Unsafe || loaded.Status is null)
            {
                throw new InvalidOperationException(
                    "Persisted supervisor identity cannot be trusted. " +
                    $"Refusing to {operation}.");
            }

            var recordedState = Inspect(loaded.Status);
            if (recordedState == RecordedSupervisorState.Active)
            {
                throw new InvalidOperationException(
                    $"A recorded Continuity supervisor is still active. Refusing to {operation}.");
            }
            if (recordedState == RecordedSupervisorState.Unsafe)
            {
                throw new InvalidOperationException(
                    $"The recorded Continuity supervisor identity could not be verified. " +
                    $"Refusing to {operation}.");
            }
        }
    }

    internal static RecordedSupervisorState Inspect(SupervisorStatus status)
    {
        if (status.State is "stopped" or "foreignEndpoint")
        {
            return RecordedSupervisorState.Stale;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(status.SupervisorProcessId);
        }
        catch (ArgumentException)
        {
            return RecordedSupervisorState.Stale;
        }

        using (process)
        {
            try
            {
                if (process.HasExited)
                {
                    return RecordedSupervisorState.Stale;
                }
                var startedAtUtc = process.StartTime.ToUniversalTime();
                if (status.SupervisorStartedAtUtc is { } recordedStart &&
                    status.SupervisorExecutable is { } recordedExecutable)
                {
                    var executable = process.MainModule?.FileName;
                    return startedAtUtc == recordedStart &&
                        executable is not null && PathsEqual(executable, recordedExecutable)
                            ? RecordedSupervisorState.Active
                            : RecordedSupervisorState.Stale;
                }

                if (!process.ProcessName.Equals(
                        "CodexContinuity",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return RecordedSupervisorState.Stale;
                }
                return startedAtUtc <= status.UpdatedAtUtc
                    ? RecordedSupervisorState.Active
                    : RecordedSupervisorState.Unsafe;
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or
                    NotSupportedException or Win32Exception)
            {
                return RecordedSupervisorState.Unsafe;
            }
        }
    }

    private static bool PathsEqual(string first, string second) =>
        Path.GetFullPath(first).Equals(
            Path.GetFullPath(second),
            StringComparison.OrdinalIgnoreCase);
}
