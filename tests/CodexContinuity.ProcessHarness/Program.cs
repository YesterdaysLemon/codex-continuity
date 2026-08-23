using CodexContinuity;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CodexContinuity.ProcessHarness;

public sealed class HarnessMarker;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "fake-self-test-app-server")
        {
            return FakeSelfTestAppServer.RunAsync(
                int.Parse(args[1], CultureInfo.InvariantCulture),
                args[2]).GetAwaiter().GetResult();
        }

        if (args.FirstOrDefault() == "socket-owner-server")
        {
            return RunSocketOwnerServerAsync(
                int.Parse(args[1], CultureInfo.InvariantCulture),
                args[2]).GetAwaiter().GetResult();
        }

        if (args.FirstOrDefault() == "fake-app-server")
        {
            return RunFakeAppServerAsync(
                int.Parse(args[1], CultureInfo.InvariantCulture),
                args[2],
                args.Length > 3
                    ? int.Parse(args[3], CultureInfo.InvariantCulture)
                    : 0,
                args.Length > 4 ? args[4] : null).GetAwaiter().GetResult();
        }

        if (args.FirstOrDefault() == "process-group-parent")
        {
            return RunProcessGroupParent(args[1]);
        }

        if (args.FirstOrDefault() == "process-group-child")
        {
            return RunProcessGroupChild(args[1], args[2], args[3]);
        }

        if (args.FirstOrDefault() == "idle-process")
        {
            File.WriteAllText(args[1], "ready");
            Thread.Sleep(Timeout.InfiniteTimeSpan);
            return 0;
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

    private static async Task<int> RunSocketOwnerServerAsync(int port, string readyPath)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var stagingPath = $"{readyPath}.tmp";
        await File.WriteAllTextAsync(
            stagingPath,
            boundPort.ToString(CultureInfo.InvariantCulture));
        File.Move(stagingPath, readyPath);
        using var client = await listener.AcceptTcpClientAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task<int> RunFakeAppServerAsync(
        int port,
        string readyPath,
        int exitAfterRequests,
        string? startGatePath)
    {
        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        var listener = new TcpListener(IPAddress.Loopback, port);
        await File.WriteAllTextAsync(
            readyPath,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        var requestCount = 0;
        try
        {
            while (startGatePath is not null && !File.Exists(startGatePath))
            {
                await Task.Delay(25, shutdown.Token);
            }
            listener.Start();
            while (!shutdown.IsCancellationRequested)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(shutdown.Token);
                    try
                    {
                        await RespondToReadyRequestAsync(client, port, shutdown.Token);
                        requestCount++;
                        if (requestCount == exitAfterRequests)
                        {
                            return 17;
                        }
                    }
                    catch (Exception exception) when (
                        exception is IOException or SocketException)
                    {
                    }
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }
            }
            return 0;
        }
        finally
        {
            listener.Stop();
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task RespondToReadyRequestAsync(
        TcpClient client,
        int port,
        CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        string? line;
        do
        {
            line = await reader.ReadLineAsync(cancellationToken);
        }
        while (!string.IsNullOrEmpty(line));

        var body = Encoding.UTF8.GetBytes($"backend:{port}");
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    private static int RunProcessGroupParent(string testDirectory)
    {
        using var unlistedHandle = new AnonymousPipeServerStream(
            PipeDirection.In,
            HandleInheritability.Inheritable);
        var unlistedClientHandle = unlistedHandle.GetClientHandleAsString();
        var primaryDirectory = Path.Combine(testDirectory, "primary");
        var unrelatedDirectory = Path.Combine(testDirectory, "unrelated");
        Directory.CreateDirectory(primaryDirectory);
        Directory.CreateDirectory(unrelatedDirectory);
        using var primary = StartProcessGroupChild(primaryDirectory, unlistedClientHandle);
        using var unrelated = StartProcessGroupChild(unrelatedDirectory, unlistedClientHandle);
        unlistedHandle.DisposeLocalCopyOfClientHandle();
        try
        {
            if (!WaitForMarker(primaryDirectory, "ready.txt") ||
                !WaitForMarker(unrelatedDirectory, "ready.txt"))
            {
                return 3;
            }

            var unrelatedHeartbeat = new FileInfo(
                Path.Combine(unrelatedDirectory, "heartbeat.txt")).Length;
            primary.SendCtrlBreak();
            if (!WaitForExit(primary))
            {
                return 4;
            }
            var unrelatedHeartbeatAdvanced = SpinWait.SpinUntil(
                () => new FileInfo(
                    Path.Combine(unrelatedDirectory, "heartbeat.txt")).Length >
                    unrelatedHeartbeat,
                TimeSpan.FromSeconds(2));
            var unrelatedProcessStayedRunning =
                unrelatedHeartbeatAdvanced &&
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
                UnlistedHandleInherited = bool.Parse(
                    File.ReadAllText(Path.Combine(primaryDirectory, "inherited.txt"))),
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
    private static WindowsProcessGroup StartProcessGroupChild(
        string testDirectory,
        string unlistedClientHandle)
    {
        var startInfo = new ProcessStartInfo(
            Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not resolve the .NET host path."))
        {
            UseShellExecute = false,
            WorkingDirectory = testDirectory,
        };
        startInfo.ArgumentList.Add(typeof(HarnessMarker).Assembly.Location);
        startInfo.ArgumentList.Add("process-group-child");
        startInfo.ArgumentList.Add(testDirectory);
        startInfo.ArgumentList.Add("quoted \"value\" with trailing slash \\");
        startInfo.ArgumentList.Add(unlistedClientHandle);
        startInfo.Environment["CONTINUITY_PROCESS_GROUP_SENTINEL"] = "continuity-\u96ea";
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
    private static int RunProcessGroupChild(
        string testDirectory,
        string payload,
        string unlistedClientHandle)
    {
        using var stopped = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            File.WriteAllText(Path.Combine(testDirectory, "signal.txt"), eventArgs.SpecialKey.ToString());
            stopped.Set();
        };
        var consoleWindow = GetConsoleWindow();
        var unlistedHandle = new IntPtr(
            long.Parse(unlistedClientHandle, CultureInfo.InvariantCulture));
        File.WriteAllText(
            Path.Combine(testDirectory, "visible.txt"),
            (consoleWindow != IntPtr.Zero && IsWindowVisible(consoleWindow)).ToString());
        File.WriteAllText(
            Path.Combine(testDirectory, "inherited.txt"),
            GetHandleInformation(unlistedHandle, out _).ToString());
        var environmentValue = Environment.GetEnvironmentVariable(
            "CONTINUITY_PROCESS_GROUP_SENTINEL");
        Console.OutputEncoding = Encoding.UTF8;
        Console.Out.Write($"out:{payload}|{environmentValue}");
        Console.Error.Write($"error:{payload}|{environmentValue}");
        var heartbeatPath = Path.Combine(testDirectory, "heartbeat.txt");
        File.WriteAllText(heartbeatPath, ".");
        File.WriteAllText(Path.Combine(testDirectory, "ready.txt"), "ready");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (stopped.Wait(TimeSpan.FromMilliseconds(25)))
            {
                return 0;
            }
            File.AppendAllText(heartbeatPath, ".");
        }
        return 5;
    }
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(IntPtr handle, out uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
