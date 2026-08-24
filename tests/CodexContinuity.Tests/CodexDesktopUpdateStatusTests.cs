using Xunit;

namespace CodexContinuity.Tests;

public sealed class CodexDesktopUpdateStatusTests
{
    [Fact]
    public void NewerManifestDoesNotClaimMicrosoftStoreAvailability()
    {
        var status = CodexDesktopUpdateStatus.Assess("26.818.5229.0", "26.818.8289.0");

        Assert.True(status.ManifestNewerThanInstalled);
        Assert.Equal("notChecked", status.MicrosoftStoreAvailability);
        Assert.Equal("checkMicrosoftStore", status.RecommendedAction);
        Assert.Contains("not proven", status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("26.818.5229.0", status.ToJson()["installedVersion"]!.GetValue<string>());
        Assert.True(status.ToJson()["manifestNewerThanInstalled"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("26.818.8289.0", "26.818.8289.0")]
    [InlineData("26.819.100.0", "26.818.8289.0")]
    public void InstalledAtOrAboveManifestNeedsNoUpdateAction(
        string installedVersion,
        string advertisedVersion)
    {
        var status = CodexDesktopUpdateStatus.Assess(installedVersion, advertisedVersion);

        Assert.False(status.ManifestNewerThanInstalled);
        Assert.Equal("notChecked", status.MicrosoftStoreAvailability);
        Assert.Null(status.RecommendedAction);
    }

    [Theory]
    [InlineData(null, "26.818.8289.0", "installed")]
    [InlineData("unknown", "26.818.8289.0", "installed")]
    [InlineData("26.818.5229.0", null, "advertised")]
    [InlineData("26.818.5229.0", "latest", "advertised")]
    public void MissingOrInvalidVersionsRemainUnknown(
        string? installedVersion,
        string? advertisedVersion,
        string expectedDetail)
    {
        var status = CodexDesktopUpdateStatus.Assess(installedVersion, advertisedVersion);

        Assert.Null(status.ManifestNewerThanInstalled);
        Assert.Equal("notChecked", status.MicrosoftStoreAvailability);
        Assert.Null(status.RecommendedAction);
        Assert.Contains(expectedDetail, status.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
