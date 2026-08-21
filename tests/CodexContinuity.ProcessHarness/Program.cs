using CodexContinuity;
using System.Diagnostics;

namespace CodexContinuity.ProcessHarness;

public sealed class HarnessMarker;

internal static class Program
{
    private static int Main(string[] args)
    {
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
}
