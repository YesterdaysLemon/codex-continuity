using CodexContinuity;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexContinuity.ProcessHarness;

public sealed class HarnessMarker;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "process-group-parent")
        {
            return RunProcessGroupParent(args[1]);
        }

        if (args.FirstOrDefault() == "process-group-child")
        {
            return RunProcessGroupChild(args[1]);
        }

        if (args.FirstOrDefault() is "self-test" or "install")
        {
            var recordPath = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "record-path.txt"));
            File.AppendAllLines(recordPath, [string.Join(' ', args)]);
            if (args[0] == "install" && args.Contains("--start-now"))
            {
                var powershell = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32",
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe");
                using var persistentChild = DetachedProcessLauncher.Start(
                    powershell,
                    ["-NoProfile", "-Command", "Start-Sleep -Seconds 15"],
                    Environment.CurrentDirectory);
                File.AppendAllLines(recordPath, [$"child-pid:{persistentChild.Id}"]);
            }
            return 0;
        }

        if (args.Length < 2)
        {
            return 2;
        }

        if (args[0] == "environment")
        {
            var startInfo = new ProcessStartInfo(args[1])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            FutureProcessEnvironment.ApplyTo(startInfo);
            foreach (var argument in args.Skip(2))
            {
                startInfo.ArgumentList.Add(argument);
            }
            using var environmentChild = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start environment child.");
            var output = environmentChild.StandardOutput.ReadToEndAsync();
            var error = environmentChild.StandardError.ReadToEndAsync();
            environmentChild.WaitForExit();
            Console.Out.Write(output.GetAwaiter().GetResult());
            Console.Error.Write(error.GetAwaiter().GetResult());
            return environmentChild.ExitCode;
        }

        using var child = DetachedProcessLauncher.Start(
            args[1],
            args.Skip(2),
            Environment.CurrentDirectory);
        Console.WriteLine(child.Id);
        return 0;
    }

    private static int RunProcessGroupParent(string testDirectory)
    {
        var primaryDirectory = Path.Combine(testDirectory, "primary");
        var unrelatedDirectory = Path.Combine(testDirectory, "unrelated");
        Directory.CreateDirectory(primaryDirectory);
        Directory.CreateDirectory(unrelatedDirectory);
        using var primary = StartProcessGroupChild(primaryDirectory);
        using var unrelated = StartProcessGroupChild(unrelatedDirectory);
        try
        {
            if (!WaitForMarker(primaryDirectory, "ready.txt") ||
                !WaitForMarker(unrelatedDirectory, "ready.txt"))
            {
                return 3;
            }

            primary.SendCtrlBreak();
            if (!WaitForExit(primary))
            {
                return 4;
            }
            var unrelatedProcessStayedRunning =
                !unrelated.HasExited &&
                !File.Exists(Path.Combine(unrelatedDirectory, "signal.txt"));
            unrelated.SendCtrlBreak();
            if (!WaitForExit(unrelated))
            {
                return 5;
            }

            var output = primary.StandardOutput.ReadToEndAsync().GetAwaiter().GetResult();
            var error = primary.StandardError.ReadToEndAsync().GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                primary.ExitCode,
                ControlEvent = File.ReadAllText(Path.Combine(primaryDirectory, "signal.txt")),
                ConsoleVisible = bool.Parse(
                    File.ReadAllText(Path.Combine(primaryDirectory, "visible.txt"))),
                UnrelatedProcessStayedRunning = unrelatedProcessStayedRunning,
                Output = output,
                Error = error,
            }));
            return 0;
        }
        finally
        {
            StopForCleanup(primary);
            StopForCleanup(unrelated);
        }
    }

    private static WindowsProcessGroup StartProcessGroupChild(string testDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet.exe")
        {
            UseShellExecute = false,
            WorkingDirectory = testDirectory,
        };
        startInfo.ArgumentList.Add(typeof(HarnessMarker).Assembly.Location);
        startInfo.ArgumentList.Add("process-group-child");
        startInfo.ArgumentList.Add(testDirectory);
        return WindowsProcessGroup.Start(startInfo);
    }

    private static bool WaitForMarker(string directory, string filename) =>
        SpinWait.SpinUntil(
            () => File.Exists(Path.Combine(directory, filename)),
            TimeSpan.FromSeconds(5));

    private static bool WaitForExit(WindowsProcessGroup process)
    {
        try
        {
            process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static void StopForCleanup(WindowsProcessGroup process)
    {
        if (!process.HasExited)
        {
            process.Kill();
            process.WaitForExitAsync().GetAwaiter().GetResult();
        }
    }

    private static int RunProcessGroupChild(string testDirectory)
    {
        using var stopped = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            File.WriteAllText(Path.Combine(testDirectory, "signal.txt"), eventArgs.SpecialKey.ToString());
            stopped.Set();
        };
        var consoleWindow = GetConsoleWindow();
        File.WriteAllText(
            Path.Combine(testDirectory, "visible.txt"),
            (consoleWindow != IntPtr.Zero && IsWindowVisible(consoleWindow)).ToString());
        File.WriteAllText(Path.Combine(testDirectory, "ready.txt"), "ready");
        return stopped.Wait(TimeSpan.FromSeconds(10)) ? 0 : 5;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
