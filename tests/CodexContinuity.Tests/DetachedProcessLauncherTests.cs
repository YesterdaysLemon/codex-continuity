using CodexContinuity.ProcessHarness;
using System.Diagnostics;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class DetachedProcessLauncherTests
{
    [Fact]
    public async Task LongLivedChildDoesNotKeepCapturedInstallerPipesOpen()
    {
        var harnessAssembly = typeof(HarnessMarker).Assembly.Location;
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo("dotnet.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     harnessAssembly,
                     "detach",
                     powershell,
                     "-NoProfile",
                     "-Command",
                     "Start-Sleep -Seconds 15",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var harness = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start process harness.");
        var childProcessId = int.Parse(
            await harness.StandardOutput.ReadLineAsync(CancellationToken.None)
                ?? throw new InvalidOperationException("Harness returned no child process id."));
        try
        {
            await harness.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var remainingOutput = harness.StandardOutput.ReadToEndAsync();
            var remainingError = harness.StandardError.ReadToEndAsync();
            await Task.WhenAll(remainingOutput, remainingError).WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(0, harness.ExitCode);
            Assert.Equal(string.Empty, await remainingOutput);
            Assert.Equal(string.Empty, await remainingError);
            using var child = Process.GetProcessById(childProcessId);
            Assert.False(child.HasExited);
        }
        finally
        {
            try
            {
                using var child = Process.GetProcessById(childProcessId);
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
            }
            catch (ArgumentException)
            {
            }
        }
    }
}
