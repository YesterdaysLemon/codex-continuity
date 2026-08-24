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

internal enum FirstAttachmentAction
{
    Start,
    Arm,
    Defer,
}

internal sealed record FirstAttachmentPlan(
    FirstAttachmentAction Action,
    IReadOnlyList<CodexDesktopProcessIdentity> WaitForProcesses,
    string Detail);

internal static class DesktopRetargetCapability
{
    private static readonly HashSet<string> InspectedBuilds = new(StringComparer.Ordinal)
    {
        "26.818.2872.0",
        "26.818.4152.0",
    };

    internal static DesktopRetargetAssessment Assess(string? installedVersion) =>
        IsFirstAttachmentVerified(installedVersion)
            ? new(
                DesktopRetargetSupport.Unsupported,
                "nextNaturalLaunch",
                "The inspected desktop creates its local app-server connection once and reconnects to the same captured transport URL.")
            : new(
                DesktopRetargetSupport.Unknown,
                "nextNaturalLaunch",
                "This desktop build has not been inspected for a supported in-process retarget interface; Continuity fails closed.");

    internal static bool IsFirstAttachmentVerified(string? installedVersion) =>
        installedVersion is not null && InspectedBuilds.Contains(installedVersion);

    internal static FirstAttachmentPlan PlanFirstAttachment(
        string? installedVersion,
        CodexDesktopObservation observation)
    {
        if (!IsFirstAttachmentVerified(installedVersion))
        {
            return new(
                FirstAttachmentAction.Defer,
                [],
                $"Codex desktop build {installedVersion ?? "unknown"} has not been verified. No supervisor was started.");
        }
        return observation.Kind switch
        {
            CodexDesktopObservationKind.NotRunning => new(
                FirstAttachmentAction.Start,
                [],
                "No existing Codex desktop process blocks the supervised backend."),
            CodexDesktopObservationKind.Running => new(
                FirstAttachmentAction.Arm,
                observation.Processes,
                observation.Detail),
            CodexDesktopObservationKind.Unsafe => new(
                FirstAttachmentAction.Defer,
                [],
                $"{observation.Detail} No supervisor was started."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(observation),
                observation.Kind,
                null),
        };
    }
}
