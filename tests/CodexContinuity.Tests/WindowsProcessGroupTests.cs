using CodexContinuity.ProcessHarness;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class WindowsProcessGroupTests
{
    [Fact]
    public async Task SendsCtrlBreakToHiddenChildProcessGroup()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"codex continuity process group {Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            var startInfo = new ProcessStartInfo("dotnet.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(typeof(HarnessMarker).Assembly.Location);
            startInfo.ArgumentList.Add("process-group-parent");
            startInfo.ArgumentList.Add(testDirectory);

            using var harness = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start process harness.");
            var output = harness.StandardOutput.ReadToEndAsync();
            var error = harness.StandardError.ReadToEndAsync();
            await harness.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(0, harness.ExitCode);
            Assert.Equal(string.Empty, await error);
            var result = JsonSerializer.Deserialize<ProcessGroupResult>(await output);
            Assert.Equal(
                new ProcessGroupResult(
                    ExitCode: 0,
                    ControlEvent: "ControlBreak",
                    ConsoleVisible: false,
                    UnrelatedProcessStayedRunning: true,
                    Output: string.Empty,
                    Error: string.Empty),
                result);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("quoted\"value", "\"quoted\\\"value\"")]
    public void QuotesWindowsArguments(string argument, string expected)
    {
        Assert.Equal(expected, WindowsProcessGroup.QuoteArgument(argument));
    }

    [Fact]
    public void DoublesTrailingSlashBeforeClosingQuote()
    {
        Assert.Equal(
            $"\"trailing slash {new string('\\', 2)}\"",
            WindowsProcessGroup.QuoteArgument("trailing slash \\"));
    }

    private sealed record ProcessGroupResult(
        int ExitCode,
        string ControlEvent,
        bool ConsoleVisible,
        bool UnrelatedProcessStayedRunning,
        string Output,
        string Error);
}
