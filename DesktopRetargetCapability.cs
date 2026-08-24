namespace CodexContinuity;

internal enum DesktopRetargetSupport
{
    Unsupported,
    Unknown,
}

internal sealed record DesktopRetargetAssessment(
    DesktopRetargetSupport Support,
    string Activation,
    string Evidence);

internal static class DesktopRetargetCapability
{
    private static readonly HashSet<string> InspectedBuilds = new(StringComparer.Ordinal)
    {
        "26.818.2872.0",
        "26.818.4152.0",
    };

    internal static DesktopRetargetAssessment Assess(string? installedVersion) =>
        installedVersion is not null && InspectedBuilds.Contains(installedVersion)
            ? new(
                DesktopRetargetSupport.Unsupported,
                "nextNaturalLaunch",
                "The inspected desktop creates its local app-server connection once and reconnects to the same captured transport URL.")
            : new(
                DesktopRetargetSupport.Unknown,
                "nextNaturalLaunch",
                "This desktop build has not been inspected for a supported in-process retarget interface; Continuity fails closed.");
}
