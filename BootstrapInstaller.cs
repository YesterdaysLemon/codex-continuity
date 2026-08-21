using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CodexContinuity;

internal sealed record BootstrapRelease(
    string Version,
    string ArchiveUrl,
    string ChecksumUrl);

internal static partial class BootstrapInstaller
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    internal static BootstrapRelease ResolveRelease(string? downloadBaseUrl = null)
    {
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version
            ?? throw new InvalidOperationException("The setup executable has no product version.");
        var version = $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        var releaseBaseUrl = string.IsNullOrWhiteSpace(downloadBaseUrl)
            ? $"https://github.com/YesterdaysLemon/codex-continuity/releases/download/v{version}"
            : downloadBaseUrl.TrimEnd('/');
        const string archiveName = "CodexContinuity-win-x64.zip";
        return new BootstrapRelease(
            version,
            $"{releaseBaseUrl}/{archiveName}",
            $"{releaseBaseUrl}/{archiveName}.sha256");
    }

    internal static string ParseSha256(string checksumText)
    {
        var match = Sha256Regex().Match(checksumText);
        return match.Success
            ? match.Groups[1].Value.ToLowerInvariant()
            : throw new InvalidDataException(
                "The published checksum file does not contain a SHA-256 digest.");
    }

    internal static async Task<int> RunAsync(
        TrayInstallMode trayInstallMode,
        bool startNow,
        bool skipSelfTest,
        bool quiet,
        string? downloadBaseUrl)
    {
        var release = ResolveRelease(downloadBaseUrl);
        var workRoot = Path.Combine(
            Path.GetTempPath(),
            $"codex-continuity-setup-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(workRoot, "CodexContinuity-win-x64.zip");
        var checksumPath = $"{archivePath}.sha256";
        var extractPath = Path.Combine(workRoot, "extracted");
        try
        {
            Directory.CreateDirectory(workRoot);
            Report(quiet, $"Downloading Codex Continuity {release.Version}…");
            await DownloadAsync(release.ArchiveUrl, archivePath);
            await DownloadAsync(release.ChecksumUrl, checksumPath);

            var expectedHash = ParseSha256(await File.ReadAllTextAsync(checksumPath));
            var actualHash = await ComputeSha256Async(archivePath);
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"SHA-256 mismatch. Expected {expectedHash} but downloaded {actualHash}.");
            }
            Report(quiet, "Release checksum verified.");

            ZipFile.ExtractToDirectory(archivePath, extractPath);
            var supervisors = Directory.GetFiles(
                extractPath,
                "CodexContinuity.exe",
                SearchOption.AllDirectories);
            if (supervisors.Length != 1)
            {
                throw new InvalidDataException(
                    $"Expected exactly one CodexContinuity.exe; found {supervisors.Length}.");
            }
            var supervisor = supervisors[0];
            var tray = Path.Combine(
                Path.GetDirectoryName(supervisor)
                    ?? throw new InvalidDataException("Release executable has no directory."),
                "CodexContinuity.Tray.exe");
            if (trayInstallMode == TrayInstallMode.Enabled && !File.Exists(tray))
            {
                throw new InvalidDataException("The release is missing CodexContinuity.Tray.exe.");
            }

            if (!skipSelfTest)
            {
                Report(quiet, "Running isolated reconnect proof…");
                var selfTestExitCode = await RunChildAsync(supervisor, ["self-test"], quiet);
                if (selfTestExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"The isolated reconnect self-test failed with exit code {selfTestExitCode}.");
                }
            }

            var installArguments = new List<string> { "install" };
            if (startNow)
            {
                installArguments.Add("--start-now");
            }
            if (trayInstallMode == TrayInstallMode.Disabled)
            {
                installArguments.Add("--no-tray");
            }
            var installExitCode = await RunChildAsync(supervisor, installArguments, quiet);
            if (installExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Installation failed with exit code {installExitCode}.");
            }
            Report(quiet, "Codex Continuity installed. The desktop app was not restarted.");
            return 0;
        }
        finally
        {
            DeleteVerifiedTemporaryDirectory(workRoot);
        }
    }

    private static async Task DownloadAsync(string url, string destination)
    {
        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(destination);
        await source.CopyToAsync(output);
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static async Task<int> RunChildAsync(
        string executable,
        IEnumerable<string> arguments,
        bool quiet)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = quiet,
            RedirectStandardOutput = quiet,
            RedirectStandardError = quiet,
            WorkingDirectory = Path.GetDirectoryName(executable),
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        var outputTask = quiet
            ? process.StandardOutput.ReadToEndAsync()
            : Task.FromResult(string.Empty);
        var errorTask = quiet
            ? process.StandardError.ReadToEndAsync()
            : Task.FromResult(string.Empty);
        await process.WaitForExitAsync();
        await Task.WhenAll(outputTask, errorTask);
        return process.ExitCode;
    }

    private static void DeleteVerifiedTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        var fullPath = Path.GetFullPath(path);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        if (!fullPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith(
                "codex-continuity-setup-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to remove unexpected temporary path: {fullPath}");
        }
        Directory.Delete(fullPath, recursive: true);
    }

    private static void Report(bool quiet, string message)
    {
        if (!quiet)
        {
            Console.WriteLine(message);
        }
    }

    [GeneratedRegex("(?im)^([0-9a-f]{64})(?:\\s+|$)")]
    private static partial Regex Sha256Regex();
}
