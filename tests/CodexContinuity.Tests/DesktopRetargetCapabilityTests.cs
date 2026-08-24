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
        Assert.True(DesktopRetargetCapability.IsFirstAttachmentVerified(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("26.999.1.0")]
    public void UninspectedBuildsFailClosed(string? version)
    {
        var assessment = DesktopRetargetCapability.Assess(version);

        Assert.Equal(DesktopRetargetSupport.Unknown, assessment.Support);
        Assert.Equal("nextNaturalLaunch", assessment.Activation);
        Assert.False(DesktopRetargetCapability.IsFirstAttachmentVerified(version));
    }

    [Fact]
    public void RunningDesktopOnVerifiedBuildArmsExactObservedProcesses()
    {
        var processes = new[] { new CodexDesktopProcessIdentity(12, 1200) };

        var plan = DesktopRetargetCapability.PlanFirstAttachment(
            "26.818.4152.0",
            new(CodexDesktopObservationKind.Running, processes, "Desktop running."));

        Assert.Equal(FirstAttachmentAction.Arm, plan.Action);
        Assert.Equal(processes, plan.WaitForProcesses);
    }

    [Fact]
    public void EmptyDesktopOnVerifiedBuildStartsImmediately()
    {
        var plan = DesktopRetargetCapability.PlanFirstAttachment(
            "26.818.4152.0",
            new(CodexDesktopObservationKind.NotRunning, [], "Desktop absent."));

        Assert.Equal(FirstAttachmentAction.Start, plan.Action);
        Assert.Empty(plan.WaitForProcesses);
    }

    [Theory]
    [InlineData("26.999.1.0", 0)]
    [InlineData("26.818.4152.0", 2)]
    public void UnknownBuildOrUnsafeObservationDefersWithoutWaitProcesses(
        string version,
        int observationKind)
    {
        var plan = DesktopRetargetCapability.PlanFirstAttachment(
            version,
            new((CodexDesktopObservationKind)observationKind, [], "Inspection unavailable."));

        Assert.Equal(FirstAttachmentAction.Defer, plan.Action);
        Assert.Empty(plan.WaitForProcesses);
    }
}
