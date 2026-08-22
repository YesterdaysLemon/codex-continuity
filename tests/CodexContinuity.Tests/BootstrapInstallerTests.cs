using CodexContinuity;
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

        Assert.Equal("0.2.1", release.Version);
        Assert.Equal(
            "https://github.com/YesterdaysLemon/codex-continuity/releases/download/v0.2.1/CodexContinuity-win-x64.zip",
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

}
