using CodexContinuity.ProcessHarness;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
    public async Task AttachmentObservesExactTargetExitWithoutOwningItsLifetime()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"codex-continuity-attachment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var releasePath = Path.Combine(testDirectory, "release.txt");
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
        startInfo.ArgumentList.Add(
            $"while (-not (Test-Path -LiteralPath '{releasePath}')) {{ Start-Sleep -Milliseconds 20 }}; exit 259");
        WindowsProcessGroup? owner = null;
        try
        {
            owner = WindowsProcessGroup.Start(startInfo);
            using (var nonOwning = WindowsProcessGroup.Attach(owner.Id))
            {
                Assert.Equal(owner.Id, nonOwning.Id);
                Assert.Equal(owner.StartedAtUtc, nonOwning.StartedAtUtc);
                Assert.Equal(owner.ExecutablePath, nonOwning.ExecutablePath);
                Assert.False(nonOwning.HasExited);
            }
            Assert.False(owner.HasExited);

            using var observer = WindowsProcessGroup.Attach(owner.Id);
            File.WriteAllText(releasePath, string.Empty);
            await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(observer.HasExited);
            Assert.Equal(259, observer.ExitCode);
        }
        finally
        {
            if (owner is { HasExited: false })
            {
                owner.Kill();
                await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            owner?.Dispose();
            DeleteTestDirectory(testDirectory);
        }
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

    [Fact]
    public void RetriesBoundedTcpTableGrowth()
    {
        const int rowBytes = sizeof(int) + (6 * sizeof(uint));
        const int port = 45123;
        var calls = 0;

        uint ReadGrowingTable(IntPtr table, ref int size)
        {
            calls++;
            size = rowBytes;
            if (calls < 3)
            {
                return 122;
            }
            Marshal.WriteInt32(table, 0, 1);
            Marshal.WriteInt32(table, 4, 2);
            Marshal.WriteInt32(
                table,
                8,
                BitConverter.ToInt32(IPAddress.Loopback.GetAddressBytes()));
            Marshal.WriteInt32(
                table,
                12,
                BitConverter.ToInt32([(byte)(port >> 8), (byte)(port & 0xff), 0, 0]));
            Marshal.WriteInt32(table, 24, Environment.ProcessId);
            return 0;
        }

        Assert.True(WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy(
            port,
            Environment.ProcessId,
            ReadGrowingTable));
        Assert.Equal(3, calls);
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
