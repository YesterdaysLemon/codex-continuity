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
        string supervisorSha256,
        string? traySha256);

    string PublishCommandExecutable(
        string sourceExecutable,
        string? sourceTrayExecutable,
        string supervisorSha256,
        string? traySha256);
}

internal sealed class InstallFileStager(string stateDirectory) : IInstallFileStager
{
    public StagedInstallVersion StageVersion(
        string sourceExecutable,
        string? sourceTrayExecutable,
        string supervisorSha256,
        string? traySha256)
    {
        var expectedSupervisorSha256 = NormalizeSha256(
            supervisorSha256,
            nameof(supervisorSha256));
        var expectedTraySha256 = ValidateTrayDigest(
            sourceTrayExecutable,
            traySha256);
        var assemblyVersion = typeof(InstallFileStager).Assembly.GetName().Version;
        var version = assemblyVersion is null
            ? "dev"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        var versionDirectory = Path.Combine(
            ContinuityPaths.VersionsDirectory(stateDirectory),
            $"{version}-{expectedSupervisorSha256[..12]}");
        Directory.CreateDirectory(versionDirectory);
        var destination = Path.Combine(versionDirectory, "CodexContinuity.exe");
        var supervisor = StageExecutable(
            sourceExecutable,
            destination,
            expectedSupervisorSha256);
        var tray = sourceTrayExecutable is null
            ? null
            : StageExecutable(
                sourceTrayExecutable,
                Path.Combine(versionDirectory, "CodexContinuity.Tray.exe"),
                expectedTraySha256!);
        return new StagedInstallVersion(supervisor, tray);
    }

    public string PublishCommandExecutable(
        string sourceExecutable,
        string? sourceTrayExecutable,
        string supervisorSha256,
        string? traySha256)
    {
        var expectedSupervisorSha256 = NormalizeSha256(
            supervisorSha256,
            nameof(supervisorSha256));
        var expectedTraySha256 = ValidateTrayDigest(
            sourceTrayExecutable,
            traySha256);
        var destination = ContinuityPaths.CommandExecutable(stateDirectory);
        PublishCommandFile(sourceExecutable, destination, expectedSupervisorSha256);
        if (sourceTrayExecutable is not null)
        {
            PublishCommandFile(
                sourceTrayExecutable,
                Path.Combine(
                    ContinuityPaths.CommandDirectory(stateDirectory),
                    "CodexContinuity.Tray.exe"),
                expectedTraySha256!);
        }
        return destination;
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string StageExecutable(
        string sourceExecutable,
        string destination,
        string expectedSha256)
    {
        if (File.Exists(destination))
        {
            if (!string.Equals(
                    ComputeSha256(destination),
                    expectedSha256,
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
                    expectedSha256,
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
        string expectedSha256)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (PathsEqual(sourceExecutable, destination))
        {
            if (!File.Exists(destination) ||
                !string.Equals(
                    ComputeSha256(destination),
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Published executable hash mismatch at {destination}.");
            }
            return;
        }
        if (File.Exists(destination) && string.Equals(
                ComputeSha256(destination),
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var temporaryPath = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(sourceExecutable, temporaryPath, overwrite: false);
            if (!string.Equals(
                    ComputeSha256(temporaryPath),
                    expectedSha256,
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

    private static string? ValidateTrayDigest(
        string? sourceTrayExecutable,
        string? traySha256)
    {
        if (sourceTrayExecutable is null)
        {
            if (traySha256 is not null)
            {
                throw new ArgumentException(
                    "A tray SHA-256 digest requires a tray executable.",
                    nameof(traySha256));
            }
            return null;
        }
        if (traySha256 is null)
        {
            throw new ArgumentException(
                "A tray executable requires a tray SHA-256 digest.",
                nameof(traySha256));
        }
        return NormalizeSha256(traySha256, nameof(traySha256));
    }

    private static string NormalizeSha256(string hash, string parameterName)
    {
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "The SHA-256 digest must contain exactly 64 hexadecimal characters.",
                parameterName);
        }
        return hash.ToLowerInvariant();
    }
}
