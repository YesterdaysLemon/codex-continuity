using CodexContinuity;
using CodexContinuity.ProcessHarness;
using System.Diagnostics;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class FutureProcessEnvironmentTests
{
    [Fact]
    public void UserEnvironmentOverridesMachineWithoutIncludingTransientProcessValues()
    {
        var machine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = @"C:\MachineBin",
            ["CODEX_HOME"] = @"C:\MachineCodex",
        };
        var user = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = @"C:\UserBin",
            ["CONTINUITY_TEST_USER_VALUE"] = "persisted",
        };

        var merged = FutureProcessEnvironment.Merge(machine, user);

        Assert.Equal(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = $@"C:\MachineBin{Path.PathSeparator}C:\UserBin",
                ["CODEX_HOME"] = @"C:\MachineCodex",
                ["CONTINUITY_TEST_USER_VALUE"] = "persisted",
            },
            merged);
    }

    [Fact]
    public async Task ProductionChildDoesNotInheritProcessOnlyCodexHome()
    {
        var persisted = FutureProcessEnvironment.Snapshot().GetValueOrDefault("CODEX_HOME") ??
            string.Empty;
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
        startInfo.Environment["CODEX_HOME"] = @"C:\transient-process-only-codex-home";
        foreach (var argument in new[]
                 {
                     typeof(HarnessMarker).Assembly.Location,
                     "environment",
                     powershell,
                     "-NoProfile",
                     "-Command",
                     "[Console]::Write($env:CODEX_HOME)",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var harness = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start environment harness.");
        var output = harness.StandardOutput.ReadToEndAsync();
        var error = harness.StandardError.ReadToEndAsync();
        await harness.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(output, error);

        Assert.Equal(0, harness.ExitCode);
        Assert.Equal(string.Empty, await error);
        Assert.Equal(persisted, await output);
        Assert.DoesNotContain("transient-process-only", await output);
    }
}
