using CodexContinuity.Contracts;

namespace CodexContinuity.Tray;

internal sealed record TrayDistributionContext(
    bool Packaged,
    string? PackageFullName);

internal static class TrayDistribution
{
    internal static TrayDistributionContext Detect()
    {
        var result = PackageIdentity.Probe();
        if (result.State == PackageIdentityState.Absent)
        {
            return new(Packaged: false, PackageFullName: null);
        }
        if (result.State != PackageIdentityState.Present || result.PackageFullName is null)
        {
            throw new InvalidOperationException(
                $"Windows package identity detection failed with status {result.Status}.");
        }
        return new(
            Packaged: true,
            PackageFullName: result.PackageFullName);
    }
}
