using CodexContinuity;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class BootstrapInstallerTests
{
    [Theory]
    [InlineData("--help", "help")]
    [InlineData("-h", "help")]
    [InlineData("--silent", "setup")]
    [InlineData("--port", "setup")]
    public void SetupExecutableResolvesReadOnlyHelpAndInstallerOptions(
        string argument,
        string expectedCommand)
    {
        Assert.Equal(expectedCommand, Program.ResolveCommand(setupExecutable: true, [argument]));
    }

    [Fact]
    public void SetupForwardsCustomPortAndLifecycleOptions()
    {
        var arguments = BootstrapInstaller.BuildInstallArguments(
            45124,
            TrayInstallMode.Disabled,
            startNow: true);

        Assert.Equal(
            ["install", "--port", "45124", "--start-now", "--no-tray"],
            arguments);
    }

    [Fact]
    public void AutomaticSetupMarksTheChildInstallIntent()
    {
        var digest = new string('a', 64);
        var arguments = BootstrapInstaller.BuildInstallArguments(
            45124,
            TrayInstallMode.Enabled,
            startNow: false,
            InstallIntent.AutomaticUpdate,
            digest);

        Assert.Equal(
            [
                "install",
                "--port",
                "45124",
                "--automatic-update",
                "--automatic-update-from-sha256",
                digest,
            ],
            arguments);
        Assert.Equal(
            InstallIntent.AutomaticUpdate,
            Program.ResolveInstallIntent([.. arguments]));
        Assert.Equal(digest, Program.ResolveAutomaticUpdateSha256([.. arguments]));
    }

    [Fact]
    public void ResolvesVersionedStableReleaseAssets()
    {
        var release = BootstrapInstaller.ResolveRelease();

        Assert.Equal("0.5.0", release.Version);
        Assert.Equal(
            "https://github.com/YesterdaysLemon/codex-continuity/releases/download/v0.5.0/CodexContinuity-win-x64.zip",
            release.ArchiveUrl);
        Assert.Equal($"{release.ArchiveUrl}.sha256", release.ChecksumUrl);
    }

    [Fact]
    public void ParsesSha256AndRejectsInvalidChecksum()
    {
        var digest = new string('a', 64);

        Assert.Equal(digest, BootstrapInstaller.ParseSha256($"{digest}  release.zip"));
        Assert.Throws<InvalidDataException>(() => BootstrapInstaller.ParseSha256("not a hash"));
    }

    [Fact]
    public async Task VerifiesMatchingChecksumAndFailsClosedOnMismatch()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "verified release");
            var expected = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("verified release")));

            await BootstrapInstaller.VerifySha256Async(path, expected);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                BootstrapInstaller.VerifySha256Async(path, new string('0', 64)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BoundedCopyRejectsContentBeyondTheLimit()
    {
        await using var source = new MemoryStream(new byte[5]);
        await using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ReleaseArchiveExtractor.CopyBoundedAsync(
                source,
                destination,
                maximumBytes: 4,
                CancellationToken.None));

        Assert.Contains("4-byte safety limit", exception.Message);
        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public async Task RejectsEntryCountFromCentralDirectoryBeforeExtraction()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var archivePath = Path.Combine(directory, "release.zip");
            CreateArchive(archivePath, ("one.txt", "1"), ("two.txt", "2"));
            var destination = Path.Combine(directory, "extracted");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ReleaseArchiveExtractor.ExtractToDirectoryAsync(
                    archivePath,
                    destination,
                    CancellationToken.None,
                    maximumEntries: 1));

            Assert.Contains("more than 1 entries", exception.Message);
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsUnderreportedCentralDirectorySize()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var archivePath = Path.Combine(directory, "release.zip");
            CreateArchive(archivePath, ("one.txt", "1234"), ("two.txt", "5678"));
            PatchZipUInt32(archivePath, 0x06054b50, fieldOffset: 12, value: 1);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ReleaseArchiveExtractor.ExtractToDirectoryAsync(
                    archivePath,
                    Path.Combine(directory, "extracted"),
                    CancellationToken.None));

            Assert.Contains("central directory is malformed", exception.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsMisalignedEocdSignatureInsideArchiveComment()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var archivePath = Path.Combine(directory, "release.zip");
            CreateArchive(archivePath, ("payload.txt", "safe"));
            AppendMisalignedEocdComment(archivePath);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ReleaseArchiveExtractor.ExtractToDirectoryAsync(
                    archivePath,
                    Path.Combine(directory, "extracted"),
                    CancellationToken.None));

            Assert.Contains("central directory is malformed", exception.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsActualExpansionBeyondForgedDeclaredLength()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var archivePath = Path.Combine(directory, "release.zip");
            CreateArchive(archivePath, ("payload.txt", "12345678"));
            PatchZipUInt32(archivePath, 0x02014b50, fieldOffset: 24, value: 1);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ReleaseArchiveExtractor.ExtractToDirectoryAsync(
                    archivePath,
                    Path.Combine(directory, "extracted"),
                    CancellationToken.None,
                    maximumExtractedBytes: 4));

            Assert.Contains("4-byte safety limit", exception.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsArchivePathTraversal()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var archivePath = Path.Combine(directory, "release.zip");
            CreateArchive(archivePath, ("../escaped.txt", "nope"));

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ReleaseArchiveExtractor.ExtractToDirectoryAsync(
                    archivePath,
                    Path.Combine(directory, "extracted"),
                    CancellationToken.None));

            Assert.Contains("escapes the extraction directory", exception.Message);
            Assert.False(File.Exists(Path.Combine(directory, "escaped.txt")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsArchiveWhoseExecutableVersionDoesNotMatchRelease()
    {
        Assert.Throws<InvalidDataException>(() => BootstrapInstaller.VerifyReleaseVersion(
            Environment.ProcessPath!,
            "99.99.99"));
    }

    [Fact]
    public async Task RejectsUnsignedAutomaticUpdatePublisherChain()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var installed = Path.Combine(directory, "installed.exe");
            var candidate = Path.Combine(directory, "candidate.exe");
            await File.WriteAllTextAsync(installed, "unsigned installed build");
            await File.WriteAllTextAsync(candidate, "unsigned candidate build");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                AuthenticodeReleaseVerifier.VerifyMatchingPublisherAsync(
                    installed,
                    [candidate],
                    CancellationToken.None));

            Assert.Contains("does not have a valid Authenticode signature", exception.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RequiresEveryCandidateToMatchTheTrustedPublisher()
    {
        var trusted = new AuthenticodeSignature("installed.exe", "Valid", "trusted");

        AuthenticodeReleaseVerifier.VerifyMatchingPublisher(
            [
                trusted,
                new AuthenticodeSignature("candidate.exe", "Valid", "trusted"),
                new AuthenticodeSignature("tray.exe", "Valid", "trusted"),
            ]);

        var unsigned = Assert.Throws<InvalidDataException>(() =>
            AuthenticodeReleaseVerifier.VerifyMatchingPublisher(
                [trusted, new AuthenticodeSignature("candidate.exe", "NotSigned", null)]));
        Assert.Contains("does not have a valid Authenticode signature", unsigned.Message);

        var differentPublisher = Assert.Throws<InvalidDataException>(() =>
            AuthenticodeReleaseVerifier.VerifyMatchingPublisher(
                [
                    trusted,
                    new AuthenticodeSignature("candidate.exe", "Valid", "trusted"),
                    new AuthenticodeSignature("tray.exe", "Valid", "different"),
                ]));
        Assert.Contains("different publisher certificate", differentPublisher.Message);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"codex-continuity-bootstrap-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateArchive(
        string archivePath,
        params (string Name, string Contents)[] entries)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var (name, contents) in entries)
        {
            using var writer = new StreamWriter(
                archive.CreateEntry(name, CompressionLevel.NoCompression).Open());
            writer.Write(contents);
        }
    }

    private static void PatchZipUInt32(
        string archivePath,
        uint signature,
        int fieldOffset,
        uint value)
    {
        var bytes = File.ReadAllBytes(archivePath);
        for (var index = bytes.Length - sizeof(uint); index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index)) != signature)
            {
                continue;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index + fieldOffset, sizeof(uint)),
                value);
            File.WriteAllBytes(archivePath, bytes);
            return;
        }
        throw new InvalidOperationException("ZIP fixture signature was not found.");
    }

    private static void AppendMisalignedEocdComment(string archivePath)
    {
        const uint eocdSignature = 0x06054b50;
        const int fakeRecordLength = 22;
        const int trailingBytes = 1;
        var bytes = File.ReadAllBytes(archivePath);
        var eocdOffset = -1;
        for (var index = bytes.Length - sizeof(uint); index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index)) == eocdSignature)
            {
                eocdOffset = index;
                break;
            }
        }
        if (eocdOffset < 0)
        {
            throw new InvalidOperationException("ZIP fixture EOCD was not found.");
        }

        var originalLength = bytes.Length;
        Array.Resize(ref bytes, originalLength + fakeRecordLength + trailingBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(eocdOffset + 20, sizeof(ushort)),
            fakeRecordLength + trailingBytes);
        var fakeRecord = bytes.AsSpan(originalLength, fakeRecordLength);
        BinaryPrimitives.WriteUInt32LittleEndian(fakeRecord, eocdSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(fakeRecord[8..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(fakeRecord[10..], 2);
        File.WriteAllBytes(archivePath, bytes);
    }

}
