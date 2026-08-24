using System.Text.Json.Nodes;

namespace CodexContinuity;

internal sealed record CodexDesktopUpdateStatus(
    string? InstalledVersion,
    string? AdvertisedVersion,
    bool? ManifestNewerThanInstalled,
    string MicrosoftStoreAvailability,
    string? RecommendedAction,
    string Detail)
{
    internal JsonObject ToJson() => new()
    {
        ["installedVersion"] = InstalledVersion,
        ["advertisedVersion"] = AdvertisedVersion,
        ["manifestNewerThanInstalled"] = ManifestNewerThanInstalled,
        ["microsoftStoreAvailability"] = MicrosoftStoreAvailability,
        ["recommendedAction"] = RecommendedAction,
        ["detail"] = Detail,
    };

    internal static CodexDesktopUpdateStatus Assess(
        string? installedVersion,
        string? advertisedVersion)
    {
        if (!Version.TryParse(installedVersion, out var installed))
        {
            return new(
                installedVersion,
                advertisedVersion,
                ManifestNewerThanInstalled: null,
                MicrosoftStoreAvailability: "notChecked",
                RecommendedAction: null,
                "The installed Codex Desktop version could not be determined.");
        }
        if (!Version.TryParse(advertisedVersion, out var advertised))
        {
            return new(
                installedVersion,
                advertisedVersion,
                ManifestNewerThanInstalled: null,
                MicrosoftStoreAvailability: "notChecked",
                RecommendedAction: null,
                "The advertised Codex Desktop version could not be determined.");
        }

        var manifestIsNewer = installed.CompareTo(advertised) < 0;
        return manifestIsNewer
            ? new(
                installedVersion,
                advertisedVersion,
                ManifestNewerThanInstalled: true,
                MicrosoftStoreAvailability: "notChecked",
                RecommendedAction: "checkMicrosoftStore",
                "A newer Codex Desktop build is advertised. Microsoft Store eligibility " +
                "is not proven until the Store checks this machine.")
            : new(
                installedVersion,
                advertisedVersion,
                ManifestNewerThanInstalled: false,
                MicrosoftStoreAvailability: "notChecked",
                RecommendedAction: null,
                "The installed Codex Desktop version is at or above the advertised build.");
    }
}
