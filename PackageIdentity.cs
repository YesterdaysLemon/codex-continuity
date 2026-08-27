using System.Runtime.InteropServices;
using System.Text;

namespace CodexContinuity.Contracts;

internal enum PackageIdentityState
{
    Absent,
    Present,
    Unknown,
}

internal sealed record PackageIdentityProbeResult(
    PackageIdentityState State,
    string? PackageFullName,
    int Status);

internal static class PackageIdentity
{
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    internal static PackageIdentityProbeResult Probe()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        if (result == AppModelErrorNoPackage)
        {
            return new(PackageIdentityState.Absent, null, result);
        }
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            return new(PackageIdentityState.Unknown, null, result);
        }

        var packageFullName = new StringBuilder(checked((int)length));
        result = GetCurrentPackageFullName(ref length, packageFullName);
        return result == 0 && packageFullName.Length > 0
            ? new(PackageIdentityState.Present, packageFullName.ToString(), result)
            : new(PackageIdentityState.Unknown, null, result);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        StringBuilder? packageFullName);
}
