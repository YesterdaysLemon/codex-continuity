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
}
