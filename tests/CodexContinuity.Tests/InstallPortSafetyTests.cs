using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class InstallPortSafetyTests
{
    [Fact]
    public async Task ManagedUninstallDoesNotPreserveAHealthyForeignEndpoint()
    {
        var legacyProbeCount = 0;

        var policy = await Program.ResolveUninstallReconnectPolicyAsync(
            managedInstalledPort: 45123,
            legacyInstalledPort: null,
            configuredUrl: LoopbackEndpoint.WebSocketUrl(45123),
            _ => Task.FromResult(false),
            _ =>
            {
                legacyProbeCount++;
                return Task.FromResult(true);
            });

        Assert.Equal(UninstallReconnectPolicy.RestoreImmediately, policy);
        Assert.Equal(0, legacyProbeCount);
    }

    [Fact]
    public async Task ManagedUninstallPreservesOnlyAVerifiedManagedEndpoint()
    {
        var policy = await Program.ResolveUninstallReconnectPolicyAsync(
            managedInstalledPort: 45123,
            legacyInstalledPort: null,
            configuredUrl: LoopbackEndpoint.WebSocketUrl(45123),
            _ => Task.FromResult(true),
            _ => Task.FromResult(false));

        Assert.Equal(UninstallReconnectPolicy.PreserveUntilNextSignIn, policy);
    }

    [Fact]
    public async Task LegacyUninstallUsesTheLegacyReadinessProbe()
    {
        var policy = await Program.ResolveUninstallReconnectPolicyAsync(
            managedInstalledPort: null,
            legacyInstalledPort: 45123,
            configuredUrl: LoopbackEndpoint.WebSocketUrl(45123),
            _ => Task.FromResult(false),
            _ => Task.FromResult(true));

        Assert.Equal(UninstallReconnectPolicy.PreserveUntilNextSignIn, policy);
    }

    [Fact]
    public async Task BlocksPortChangeWhileInstalledBackendIsReady()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Program.EnsurePortChangeIsSafeAsync(
                45123,
                45124,
                _ => Task.FromResult(true)));

        Assert.Contains("port 45123 is still ready", exception.Message);
        Assert.Contains("port 45124", exception.Message);
    }

    [Theory]
    [InlineData(null, 45124, true)]
    [InlineData(45123, 45123, true)]
    [InlineData(45123, 45124, false)]
    public async Task AllowsSafeInstallPortSelection(
        int? installedPort,
        int requestedPort,
        bool installedBackendReady)
    {
        var probeCount = 0;

        await Program.EnsurePortChangeIsSafeAsync(
            installedPort,
            requestedPort,
            _ =>
            {
                probeCount++;
                return Task.FromResult(installedBackendReady);
            });

        Assert.Equal(
            installedPort is not null && installedPort != requestedPort ? 1 : 0,
            probeCount);
    }
}
