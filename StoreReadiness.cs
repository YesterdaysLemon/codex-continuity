using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexContinuity;

internal sealed record StoreReadinessGate(
    [property: JsonPropertyName("id")]
    string Id,
    [property: JsonPropertyName("state")]
    string State,
    [property: JsonPropertyName("detail")]
    string Detail,
    [property: JsonPropertyName("evidenceOwner")]
    string EvidenceOwner);

internal sealed record StoreReadinessReport(
    [property: JsonPropertyName("schemaVersion")]
    int SchemaVersion,
    [property: JsonPropertyName("readyForSubmission")]
    bool ReadyForSubmission,
    [property: JsonPropertyName("runtimePackaging")]
    string RuntimePackaging,
    [property: JsonPropertyName("packageFullName")]
    string? PackageFullName,
    [property: JsonPropertyName("proposedPackageVersion")]
    string ProposedPackageVersion,
    [property: JsonPropertyName("updateOwner")]
    string UpdateOwner,
    [property: JsonPropertyName("updateSourceEvidence")]
    string UpdateSourceEvidence,
    [property: JsonPropertyName("restartsCodex")]
    bool RestartsCodex,
    [property: JsonPropertyName("gates")]
    IReadOnlyList<StoreReadinessGate> Gates);

internal static class StoreReadiness
{
    internal const int BlockedExitCode = 2;

    internal static StoreReadinessReport Assess(
        ContinuityDistributionContext distribution) => Assess(
            distribution,
            typeof(StoreReadiness).Assembly.GetName().Version is { } assemblyVersion
                ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}"
                : throw new InvalidOperationException("The product version is unavailable."));

    internal static StoreReadinessReport Assess(
        ContinuityDistributionContext distribution,
        string productVersion) => new(
        SchemaVersion: 1,
        ReadyForSubmission: false,
        RuntimePackaging: ContinuityDistribution.HasPackageIdentity(distribution)
                ? "packaged"
                : "direct",
        PackageFullName: distribution.PackageFullName,
        ProposedPackageVersion: StorePackageVersion.FromProductVersion(productVersion),
        UpdateOwner: ContinuityDistribution.HasPackageIdentity(distribution)
            ? "externalPackageSource"
            : "codexContinuity",
        UpdateSourceEvidence: ContinuityDistribution.HasPackageIdentity(distribution)
            ? "unverified"
            : "directReleaseFeed",
        RestartsCodex: false,
        Gates:
        [
            new(
                "fullTrustPackageModel",
                "prototype",
                "The manifest prototype declares a full-trust supervisor and a disabled startup task; " +
                "MakeAppx schema validation and Store ingestion are not yet proven.",
                "repository"),
            new(
                "folderBasedPublishLayout",
                "prototype",
                "The staging script creates folder-based self-contained supervisor and tray payloads without producing an MSIX.",
                "repository"),
            new(
                "storePackageVersionMapping",
                "ready",
                "A monotonic four-part Store version keeps the first component nonzero and the fourth component zero.",
                "repository"),
            new(
                "packagedRuntimeFailClosed",
                ContinuityDistribution.HasPackageIdentity(distribution)
                    ? "ready"
                    : "notApplicable",
                ContinuityDistribution.HasPackageIdentity(distribution)
                    ? "Package identity disables GitHub update and lifecycle mutation commands."
                    : "The current process has no Windows package identity.",
                "repository"),
            new(
                "storeOwnedUpdates",
                "blocked",
                "Windows package identity does not prove that Microsoft Store installed or updates the package.",
                "microsoft"),
            new(
                "firstRunConsent",
                "blocked",
                "No packaged first-run flow yet obtains consent before endpoint and startup changes.",
                "repository"),
            new(
                "startupTaskOwnership",
                "blocked",
                "The disabled startup declaration exists, but packaged startup currently has no dedicated supervisor command or consent flow.",
                "repository"),
            new(
                "packagedStartupEntrypoint",
                "blocked",
                "The manifest startup executable currently resolves to read-only help and packaged serve remains disabled.",
                "repository"),
            new(
                "externalCodexEndpointConfiguration",
                "blocked",
                "Codex currently exposes no documented package-owned persistent app-server endpoint setting.",
                "openAI"),
            new(
                "cleanUninstallRestoresEndpoint",
                "blocked",
                "An MSIX cannot leave CODEX_APP_SERVER_WS_URL pointing at a removed supervisor; " +
                "a package-uninstall restoration mechanism is not proven.",
                "repositoryAndPlatform"),
            new(
                "directInstallMigration",
                "blocked",
                "Direct-to-packaged ownership transfer and rollback are not implemented.",
                "repository"),
            new(
                "packagedRollbackRecovery",
                "blocked",
                "No package-source rollback, downgrade, or proven direct-lane recovery path exists.",
                "repositoryAndPlatform"),
            new(
                "activeAgentPackageUpgrade",
                "blocked",
                "A signed test package cannot be exercised until endpoint ownership and clean uninstall are proven.",
                "repository")
        ]);

    internal static int Run(
        ContinuityDistributionContext distribution,
        TextWriter output,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        var report = Assess(distribution);
        output.WriteLine(JsonSerializer.Serialize(report, jsonOptions));
        return report.ReadyForSubmission ? 0 : BlockedExitCode;
    }
}
