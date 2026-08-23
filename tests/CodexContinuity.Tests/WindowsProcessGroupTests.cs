using CodexContinuity.ProcessHarness;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
        Process? harness = null;
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

            harness = Process.Start(startInfo)
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
                    UnlistedHandleInherited: false,
                    Output: "out:quoted \"value\" with trailing slash \\|continuity-\u96ea",
                    Error: "error:quoted \"value\" with trailing slash \\|continuity-\u96ea"),
                result);
        }
        finally
        {
            if (harness is { HasExited: false })
            {
                harness.Kill(entireProcessTree: true);
                await harness.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            harness?.Dispose();
            DeleteTestDirectory(testDirectory);
        }
    }

    [Fact]
    public void RejectsRelativeExecutablePaths()
    {
        var startInfo = new ProcessStartInfo("dotnet.exe")
        {
            UseShellExecute = false,
        };

        var exception = Assert.Throws<ArgumentException>(
            () => WindowsProcessGroup.Start(startInfo));

        Assert.Contains("fully qualified", exception.Message);
    }
    [Fact]
    public void TerminatesChildWhenPostCreateSetupFails()
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        var childProcessId = 0;

        Assert.Throws<InvalidOperationException>(() =>
            WindowsProcessGroup.Start(startInfo, processId =>
            {
                childProcessId = processId;
                throw new InvalidOperationException("Injected post-create failure.");
            }));

        Assert.NotEqual(0, childProcessId);
        Assert.False(ProcessIsRunning(childProcessId));
    }

    [Theory]
    [InlineData(23)]
    [InlineData(259)]
    public async Task RetainsExitCodeWhenChildExitsImmediately(int exitCode)
    {
        var commandPrompt = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "cmd.exe");
        var startInfo = new ProcessStartInfo(commandPrompt)
        {
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add($"exit {exitCode}");

        using var process = WindowsProcessGroup.Start(startInfo);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(process.HasExited);
        Assert.Equal(exitCode, process.ExitCode);
        process.SendCtrlBreak();
    }

    [Fact]
    public async Task AttachesToExistingProcessWithoutOwningItsLifetime()
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        using var owner = WindowsProcessGroup.Start(startInfo);

        using (var attachment = WindowsProcessGroup.Attach(owner.Id))
        {
            Assert.Equal(owner.Id, attachment.Id);
            Assert.Equal(owner.StartedAtUtc, attachment.StartedAtUtc);
            Assert.Equal(owner.ExecutablePath, attachment.ExecutablePath);
            Assert.False(attachment.HasExited);
        }

        Assert.False(owner.HasExited);
        owner.Kill();
        await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ProvesLoopbackListenerProcessOwnership()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.True(WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy(
            port,
            Environment.ProcessId));
        Assert.False(WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy(
            port,
            int.MaxValue));
    }

    private static bool ProcessIsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
    private static void DeleteTestDirectory(string path)
    {
        for (var attempt = 1; Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) when (attempt < 6)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }
    }
    private sealed record ProcessGroupResult(
        int ExitCode,
        string ControlEvent,
        bool ConsoleVisible,
        bool UnrelatedProcessStayedRunning,
        bool UnlistedHandleInherited,
        string Output,
        string Error);
}
