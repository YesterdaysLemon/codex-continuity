using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class DesktopRetargetCapabilityTests
{
    [Theory]
    [InlineData("26.818.2872.0")]
    [InlineData("26.818.4152.0")]
    public void InspectedBuildsReportUnsupportedLiveRetarget(string version)
    {
        var assessment = DesktopRetargetCapability.Assess(version);

        Assert.Equal(DesktopRetargetSupport.Unsupported, assessment.Support);
        Assert.Equal("nextNaturalLaunch", assessment.Activation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("26.999.1.0")]
    public void UninspectedBuildsFailClosed(string? version)
    {
        var assessment = DesktopRetargetCapability.Assess(version);

        Assert.Equal(DesktopRetargetSupport.Unknown, assessment.Support);
        Assert.Equal("nextNaturalLaunch", assessment.Activation);
    }
}
