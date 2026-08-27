using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class StoreReadinessTests
{
    [Fact]
    public void DirectDistributionRetainsContinuityOwnedUpdates()
    {
        Assert.True(StoreRuntimePolicy.SelfUpdatesAreOwnedByContinuity(
            ContinuityDistributionContext.Direct));
        Assert.True(StoreRuntimePolicy.TryAuthorize(
            "install",
            ContinuityDistributionContext.Direct,
            out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("store-readiness")]
    [InlineData("self-test")]
    public void PackagedPrototypeAllowsOnlyReadOnlyOrIsolatedCommands(string command)
    {
        var distribution = PackagedDistribution();

        Assert.True(StoreRuntimePolicy.TryAuthorize(command, distribution, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("serve")]
    [InlineData("probe")]
    [InlineData("status")]
    [InlineData("handoff-plan")]
    [InlineData("install")]
    [InlineData("attach")]
    [InlineData("repair")]
    [InlineData("uninstall")]
    [InlineData("rollback")]
    [InlineData("setup")]
    [InlineData("update-policy")]
    public void PackagedPrototypeRejectsLifecycleMutationUntilCleanupIsProven(string command)
    {
        var distribution = PackagedDistribution();

        Assert.False(StoreRuntimePolicy.TryAuthorize(command, distribution, out var error));
        Assert.Contains("non-shippable", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagedUpdateCommandDoesNotInferMicrosoftStoreOwnership()
    {
        var distribution = PackagedDistribution();

        Assert.False(StoreRuntimePolicy.SelfUpdatesAreOwnedByContinuity(distribution));
        Assert.False(StoreRuntimePolicy.TryAuthorize("update", distribution, out var error));
        Assert.Contains("not inferred", error, StringComparison.Ordinal);
    }

    [Fact]
    public void SubmissionPreflightIsMachineReadableAndFailsClosed()
    {
        var output = new StringBuilder();
        using var writer = new StringWriter(output);

        var exitCode = StoreReadiness.Run(
            PackagedDistribution(),
            writer,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(StoreReadiness.BlockedExitCode, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.False(root.GetProperty("readyForSubmission").GetBoolean());
        Assert.Equal("packaged", root.GetProperty("runtimePackaging").GetString());
        Assert.Equal("externalPackageSource", root.GetProperty("updateOwner").GetString());
        Assert.Equal("unverified", root.GetProperty("updateSourceEvidence").GetString());
        Assert.Equal("1.7.0.0", root.GetProperty("proposedPackageVersion").GetString());
        Assert.False(root.GetProperty("restartsCodex").GetBoolean());
        var gates = root.GetProperty("gates").EnumerateArray().ToArray();
        Assert.Contains(gates, gate =>
            gate.GetProperty("id").GetString() == "cleanUninstallRestoresEndpoint" &&
            gate.GetProperty("state").GetString() == "blocked");
        Assert.Contains(gates, gate =>
            gate.GetProperty("id").GetString() == "packagedRuntimeFailClosed" &&
            gate.GetProperty("state").GetString() == "ready");
        Assert.Contains(gates, gate =>
            gate.GetProperty("id").GetString() == "storeOwnedUpdates" &&
            gate.GetProperty("state").GetString() == "blocked");
    }

    [Fact]
    public void DirectRuntimeMarksPackagedGuardNotApplicable()
    {
        var report = StoreReadiness.Assess(ContinuityDistributionContext.Direct);

        var packagedGuard = Assert.Single(
            report.Gates,
            gate => gate.Id == "packagedRuntimeFailClosed");
        Assert.Equal("notApplicable", packagedGuard.State);
        Assert.Contains(
            report.Gates,
            gate => gate.Id == "packagedRollbackRecovery" && gate.State == "blocked");
    }

    [Theory]
    [InlineData("0.7.0", "1.7.0.0")]
    [InlineData("0.7.1", "1.7.1.0")]
    [InlineData("1.0.0", "1.256.0.0")]
    [InlineData("1.2.345", "1.258.345.0")]
    public void StoreVersionMappingIsMonotonicAndCertificationCompatible(
        string productVersion,
        string expected) => Assert.Equal(
            expected,
            StorePackageVersion.FromProductVersion(productVersion));

    [Theory]
    [InlineData("0.7")]
    [InlineData("0.7.0.1")]
    [InlineData("0.256.0")]
    [InlineData("256.0.0")]
    [InlineData("0.7.65536")]
    public void StoreVersionMappingRejectsAmbiguousOrOutOfRangeVersions(string productVersion) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            StorePackageVersion.FromProductVersion(productVersion));

    [Fact]
    public void UnpackagedTestHostReportsDirectDistribution()
    {
        Assert.Equal(
            ContinuityDistributionChannel.Direct,
            ContinuityDistribution.Detect().Channel);
    }

    [Fact]
    public void ManifestPrototypeHasOneDisabledSupervisorStartupTask()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(
            repositoryRoot,
            "packaging",
            "msix",
            "Package.appxmanifest.template.xml");
        var manifest = XDocument.Load(manifestPath);
        var startupTasks = manifest
            .Descendants()
            .Where(element => element.Name.LocalName == "StartupTask")
            .ToArray();
        var capabilities = manifest
            .Descendants()
            .Where(element => element.Name.LocalName == "Capability")
            .Select(element => element.Attribute("Name")?.Value)
            .ToArray();

        var startupTask = Assert.Single(startupTasks);
        Assert.Equal("CodexContinuitySupervisor", startupTask.Attribute("TaskId")?.Value);
        Assert.Equal("false", startupTask.Attribute("Enabled")?.Value);
        Assert.Contains("runFullTrust", capabilities);
        Assert.Contains(
            manifest.Descendants(),
            element => element.Name.LocalName == "Application" &&
                element.Attribute(XName.Get(
                    "RuntimeBehavior",
                    "http://schemas.microsoft.com/appx/manifest/uap/windows10/10"))?.Value ==
                    "packagedClassicApp");
    }

    private static ContinuityDistributionContext PackagedDistribution() =>
        new(
            ContinuityDistributionChannel.Packaged,
            "YesterdaysLemon.CodexContinuity_0.7.0.0_x64__test");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexContinuity.csproj")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
