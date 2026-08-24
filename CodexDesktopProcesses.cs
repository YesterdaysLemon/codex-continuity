using System.ComponentModel;
using System.Diagnostics;

namespace CodexContinuity;

internal enum CodexDesktopObservationKind
{
    NotRunning,
    Running,
    Unsafe,
}

internal sealed record CodexDesktopProcessIdentity(int ProcessId, long StartedAtUtcTicks);

internal sealed record CodexDesktopProcessSnapshot(
    int ProcessId,
    string? ExecutablePath,
    long? StartedAtUtcTicks);

internal sealed record CodexDesktopObservation(
    CodexDesktopObservationKind Kind,
    IReadOnlyList<CodexDesktopProcessIdentity> Processes,
    string Detail);

internal enum ObservedProcessState
{
    Exited,
    Running,
    Unknown,
}

internal static class CodexDesktopProcesses
{
    private const string DesktopProcessName = "ChatGPT";
    private const string StorePackagePathMarker = @"\WindowsApps\OpenAI.Codex_";

    internal static CodexDesktopObservation Capture() =>
        Evaluate(SnapshotProcesses(DesktopProcessName));

    internal static CodexDesktopObservation Evaluate(
        IReadOnlyList<CodexDesktopProcessSnapshot> snapshots)
    {
        if (snapshots.Any(snapshot =>
                snapshot.ExecutablePath is null || snapshot.StartedAtUtcTicks is null))
        {
            return new(
                CodexDesktopObservationKind.Unsafe,
                [],
                "A ChatGPT process could not be identified safely; automatic first attachment is disabled.");
        }

        var processes = snapshots
            .Where(snapshot => IsStoreCodexDesktop(snapshot.ExecutablePath!))
            .Select(snapshot => new CodexDesktopProcessIdentity(
                snapshot.ProcessId,
                snapshot.StartedAtUtcTicks!.Value))
            .ToArray();
        return processes.Length == 0
            ? new(
                CodexDesktopObservationKind.NotRunning,
                processes,
                "No running Microsoft Store Codex desktop process was observed.")
            : new(
                CodexDesktopObservationKind.Running,
                processes,
                $"Observed {processes.Length} process(es) from the running Microsoft Store Codex desktop.");
    }

    internal static async Task WaitForExitAsync(
        IReadOnlyList<CodexDesktopProcessIdentity> processes,
        CancellationToken cancellationToken,
        Func<CodexDesktopProcessIdentity, ObservedProcessState>? inspect = null,
        TimeSpan? pollInterval = null)
    {
        inspect ??= Inspect;
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        while (processes.Any(process => inspect(process) != ObservedProcessState.Exited))
        {
            await Task.Delay(interval, cancellationToken);
        }
    }

    private static bool IsStoreCodexDesktop(string executablePath) =>
        Path.GetFileName(executablePath).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) &&
        executablePath.Contains(StorePackagePathMarker, StringComparison.OrdinalIgnoreCase);

    private static ObservedProcessState Inspect(CodexDesktopProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited)
            {
                return ObservedProcessState.Exited;
            }
            return process.StartTime.ToUniversalTime().Ticks == identity.StartedAtUtcTicks
                ? ObservedProcessState.Running
                : ObservedProcessState.Exited;
        }
        catch (ArgumentException)
        {
            return ObservedProcessState.Exited;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return ObservedProcessState.Unknown;
        }
    }

    private static IReadOnlyList<CodexDesktopProcessSnapshot> SnapshotProcesses(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        var snapshots = new List<CodexDesktopProcessSnapshot>(processes.Length);
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        snapshots.Add(new(
                            process.Id,
                            process.MainModule?.FileName,
                            process.StartTime.ToUniversalTime().Ticks));
                    }
                }
                catch (Exception exception) when (
                    exception is ArgumentException or InvalidOperationException or
                        NotSupportedException or Win32Exception)
                {
                    snapshots.Add(new(process.Id, ExecutablePath: null, StartedAtUtcTicks: null));
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
        return snapshots;
    }
}
