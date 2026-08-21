using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class InstallPortSafetyTests
{
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
