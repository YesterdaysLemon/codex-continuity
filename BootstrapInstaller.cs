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

internal sealed record TrustedInstalledBuild(string Executable, string Sha256);

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
        int port,
        TrayInstallMode trayInstallMode,
        bool startNow,
        bool skipSelfTest,
        bool quiet,
        string? downloadBaseUrl)
        => await RunReleaseAsync(
            ResolveRelease(downloadBaseUrl),
            port,
            trayInstallMode,
            startNow,
            skipSelfTest,
            quiet);

    internal static async Task<int> RunReleaseAsync(
        BootstrapRelease release,
        int port,
        TrayInstallMode trayInstallMode,
        bool startNow,
        bool skipSelfTest,
        bool quiet)
        => await RunReleaseAsync(
            release,
            port,
            trayInstallMode,
            startNow,
            skipSelfTest,
            quiet,
            CancellationToken.None);

    internal static async Task<int> RunReleaseAsync(
        BootstrapRelease release,
        int port,
        TrayInstallMode trayInstallMode,
        bool startNow,
        bool skipSelfTest,
        bool quiet,
        CancellationToken cancellationToken,
        TrustedInstalledBuild? automaticUpdateSource = null)
    {
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
            await DownloadAsync(release.ArchiveUrl, archivePath, cancellationToken);
            await DownloadAsync(release.ChecksumUrl, checksumPath, cancellationToken);

            var expectedHash = ParseSha256(await File.ReadAllTextAsync(checksumPath));
            await VerifySha256Async(archivePath, expectedHash);
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
            VerifyReleaseVersion(supervisor, release.Version);
            var tray = Path.Combine(
                Path.GetDirectoryName(supervisor)
                    ?? throw new InvalidDataException("Release executable has no directory."),
                "CodexContinuity.Tray.exe");
            if (trayInstallMode == TrayInstallMode.Enabled && !File.Exists(tray))
            {
                throw new InvalidDataException("The release is missing CodexContinuity.Tray.exe.");
            }

            if (automaticUpdateSource is not null)
            {
                var candidates = File.Exists(tray)
                    ? new[] { supervisor, tray }
                    : [supervisor];
                await AuthenticodeReleaseVerifier.VerifyMatchingPublisherAsync(
                    automaticUpdateSource.Executable,
                    candidates,
                    cancellationToken);
                Report(quiet, "Release publisher signature verified.");
            }

            if (!skipSelfTest)
            {
                Report(quiet, "Running isolated reconnect proof…");
                var selfTestExitCode = await RunChildAsync(
                    supervisor,
                    ["self-test"],
                    quiet,
                    cancellationToken);
                if (selfTestExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"The isolated reconnect self-test failed with exit code {selfTestExitCode}.");
                }
            }

            var installArguments = BuildInstallArguments(
                port,
                trayInstallMode,
                startNow);
            var installExitCode = await RunChildAsync(
                supervisor,
                installArguments,
                quiet,
                cancellationToken);
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

    internal static List<string> BuildInstallArguments(
        int port,
        TrayInstallMode trayInstallMode,
        bool startNow)
    {
        LoopbackEndpoint.ValidatePort(port);
        var arguments = new List<string> { "install", "--port", port.ToString() };
        if (startNow)
        {
            arguments.Add("--start-now");
        }
        if (trayInstallMode == TrayInstallMode.Disabled)
        {
            arguments.Add("--no-tray");
        }
        return arguments;
    }

    private static async Task DownloadAsync(
        string url,
        string destination,
        CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(destination);
        await source.CopyToAsync(output, cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    internal static async Task VerifySha256Async(string path, string expectedHash)
    {
        var actualHash = await ComputeSha256Async(path);
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SHA-256 mismatch. Expected {expectedHash} but downloaded {actualHash}.");
        }
    }

    internal static void VerifyReleaseVersion(string executable, string expectedVersion)
    {
        var version = FileVersionInfo.GetVersionInfo(executable);
        var actualVersion = $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}";
        if (!string.Equals(expectedVersion, actualVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Release version mismatch. Expected {expectedVersion} but archive contains {actualVersion}.");
        }
    }

    private static async Task<int> RunChildAsync(
        string executable,
        IEnumerable<string> arguments,
        bool quiet,
        CancellationToken cancellationToken)
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
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw;
        }
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
