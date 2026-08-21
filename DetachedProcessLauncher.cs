using System.Diagnostics;

namespace CodexContinuity;

internal static class DetachedProcessLauncher
{
    internal static Process Start(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
    }
}
