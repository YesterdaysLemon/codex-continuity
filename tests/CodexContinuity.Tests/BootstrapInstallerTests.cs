using CodexContinuity;
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
}
