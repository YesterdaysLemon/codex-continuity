using CodexContinuity;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class BootstrapInstallerTests
{
    [Fact]
    public void ResolvesVersionedStableReleaseAssets()
    {
        var release = BootstrapInstaller.ResolveRelease();

        Assert.Equal("0.2.0", release.Version);
        Assert.Equal(
            "https://github.com/YesterdaysLemon/codex-continuity/releases/download/v0.2.0/CodexContinuity-win-x64.zip",
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
