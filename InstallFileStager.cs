using System.Security.Cryptography;

namespace CodexContinuity;

internal sealed record StagedInstallVersion(
    string SupervisorExecutable,
    string? TrayExecutable);

internal interface IInstallFileStager
{
    StagedInstallVersion StageVersion(
        string sourceExecutable,
        string? sourceTrayExecutable,
        string hash);

    string PublishCommandExecutable(
        string sourceExecutable,
        string? sourceTrayExecutable,
        string hash);
}

internal sealed class InstallFileStager(string stateDirectory) : IInstallFileStager
{
    public StagedInstallVersion StageVersion(
        string sourceExecutable,
        string? sourceTrayExecutable,
        string hash)
    {
        var assemblyVersion = typeof(InstallFileStager).Assembly.GetName().Version;
        var version = assemblyVersion is null
            ? "dev"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        var versionDirectory = Path.Combine(
            ContinuityPaths.VersionsDirectory(stateDirectory),
            $"{version}-{hash[..12].ToLowerInvariant()}");
        Directory.CreateDirectory(versionDirectory);
        var destination = Path.Combine(versionDirectory, "CodexContinuity.exe");
        var supervisor = StageExecutable(sourceExecutable, destination);
        var tray = sourceTrayExecutable is null
            ? null
            : StageExecutable(
                sourceTrayExecutable,
                Path.Combine(versionDirectory, "CodexContinuity.Tray.exe"));
        return new StagedInstallVersion(supervisor, tray);
    }

    public string PublishCommandExecutable(
        string sourceExecutable,
        string? sourceTrayExecutable,
        string hash)
    {
        var destination = ContinuityPaths.CommandExecutable(stateDirectory);
        PublishCommandFile(sourceExecutable, destination, hash);
        if (sourceTrayExecutable is not null)
        {
            PublishCommandFile(
                sourceTrayExecutable,
                Path.Combine(
                    ContinuityPaths.CommandDirectory(stateDirectory),
                    "CodexContinuity.Tray.exe"),
                ComputeSha256(sourceTrayExecutable));
        }
        return destination;
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string StageExecutable(string sourceExecutable, string destination)
    {
        var sourceHash = ComputeSha256(sourceExecutable);
        if (File.Exists(destination))
        {
            if (!string.Equals(
                    ComputeSha256(destination),
                    sourceHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Staged executable hash mismatch at {destination}.");
            }
            return destination;
        }

        var temporaryPath = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(sourceExecutable, temporaryPath, overwrite: false);
            if (!string.Equals(
                    ComputeSha256(temporaryPath),
                    sourceHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Staged executable failed its SHA-256 verification.");
            }
            File.Move(temporaryPath, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        return destination;
    }

    private static void PublishCommandFile(
        string sourceExecutable,
        string destination,
        string hash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (PathsEqual(sourceExecutable, destination) ||
            (File.Exists(destination) && string.Equals(
                ComputeSha256(destination),
                hash,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        var temporaryPath = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(sourceExecutable, temporaryPath, overwrite: false);
            if (!string.Equals(
                    ComputeSha256(temporaryPath),
                    hash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Published command failed its SHA-256 verification.");
            }
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool PathsEqual(string first, string second) =>
        Path.GetFullPath(first).Equals(
            Path.GetFullPath(second),
            StringComparison.OrdinalIgnoreCase);
}
