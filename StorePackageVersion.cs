namespace CodexContinuity;

internal static class StorePackageVersion
{
    private const int ComponentRadix = 256;
    private const int MaximumComponent = ushort.MaxValue;

    internal static string FromProductVersion(string productVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);
        if (!Version.TryParse(productVersion, out var version) ||
            version.Build < 0 || version.Revision >= 0)
        {
            throw new ArgumentException(
                "The product version must have exactly three numeric components.",
                nameof(productVersion));
        }
        if (version.Major >= ComponentRadix || version.Minor >= ComponentRadix)
        {
            throw new ArgumentOutOfRangeException(
                nameof(productVersion),
                "Store mapping supports product major and minor components from 0 through 255.");
        }
        if (version.Build > MaximumComponent)
        {
            throw new ArgumentOutOfRangeException(
                nameof(productVersion),
                $"Store mapping supports a product patch component through {MaximumComponent}.");
        }

        var productLine = checked(version.Major * ComponentRadix + version.Minor);
        return $"1.{productLine}.{version.Build}.0";
    }
}
