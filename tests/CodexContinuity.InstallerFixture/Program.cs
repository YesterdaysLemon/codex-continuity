using CodexContinuity;

namespace CodexContinuity.InstallerFixture;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() is not ("self-test" or "install"))
        {
            return 2;
        }

        var recordPath = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "record-path.txt"));
        File.AppendAllLines(recordPath, [string.Join(' ', args)]);
        if (args[0] == "install" && args.Contains("--start-now"))
        {
            var persistentWorkingDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.Windows);
            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            using var persistentChild = DetachedProcessLauncher.Start(
                powershell,
                ["-NoProfile", "-Command", "Start-Sleep -Seconds 15"],
                persistentWorkingDirectory);
            File.AppendAllLines(recordPath, [$"child-pid:{persistentChild.Id}"]);
        }
        return 0;
    }
}
