using CodexContinuity.Contracts;

namespace CodexContinuity;

internal enum ContinuityDistributionChannel
{
    Direct,
    Packaged,
}

internal sealed record ContinuityDistributionContext(
    ContinuityDistributionChannel Channel,
    string? PackageFullName)
{
    internal static ContinuityDistributionContext Direct { get; } =
        new(ContinuityDistributionChannel.Direct, null);
}

internal static class ContinuityDistribution
{
    internal static ContinuityDistributionContext Detect()
    {
        var result = PackageIdentity.Probe();
        if (result.State == PackageIdentityState.Absent)
        {
            return ContinuityDistributionContext.Direct;
        }
        if (result.State != PackageIdentityState.Present || result.PackageFullName is null)
        {
            throw new InvalidOperationException(
                $"Windows package identity detection failed with status {result.Status}.");
        }
        return new(
            ContinuityDistributionChannel.Packaged,
            result.PackageFullName);
    }

    internal static bool HasPackageIdentity(ContinuityDistributionContext distribution) =>
        distribution.Channel == ContinuityDistributionChannel.Packaged;

}

internal static class StoreRuntimePolicy
{
    internal static bool SelfUpdatesAreOwnedByContinuity(
        ContinuityDistributionContext distribution) =>
        !ContinuityDistribution.HasPackageIdentity(distribution);

    internal static bool TryAuthorize(
        string command,
        ContinuityDistributionContext distribution,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(distribution);

        if (!ContinuityDistribution.HasPackageIdentity(distribution))
        {
            error = null;
            return true;
        }

        if (command is "help" or "--help" or "-h" or "store-readiness" or "self-test")
        {
            error = null;
            return true;
        }

        error = command == "update"
            ? "This packaged Continuity build does not self-update. Its package source " +
              "must deliver updates; Microsoft Store ownership is not inferred from " +
              "package identity alone."
            : "This packaged build is a non-shippable Store-readiness prototype. " +
              "Continuity will not change Codex endpoint or startup configuration until " +
              "clean uninstall restoration is proven.";
        return false;
    }
}
