using System.Text.Json;
using System.Text.Json.Nodes;
using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class DesktopMcpBridgeTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-desktop-mcp-{Guid.NewGuid():N}");
    private readonly string runtimeRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenAI",
        "Codex",
        "runtimes",
        "cua_node",
        $"continuity-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task ResolvesOnePackageBoundRuntimeAndOneStoreOwnedToolsPipe()
    {
        var fixture = CreateRuntimeFixture();
        var pipeName = $"codex-browser-use-{Guid.NewGuid():D}";
        var process = new DesktopMcpProcessIdentity(
            4321,
            fixture.DesktopExecutable,
            638900000000000000);
        var probeCalls = new List<string>();
        var checks = new DesktopMcpContractChecks(
            () => RunningObservation(process),
            () => RegistrationJson(process.ProcessId, fixture),
            processId => processId == process.ProcessId ? process : null,
            () => ["not-a-capability", pipeName],
            (candidate, owner, _) =>
            {
                probeCalls.Add(candidate);
                return Task.FromResult(
                    candidate == pipeName && owner == process.ProcessId);
            });

        var result = await DesktopMcpContractResolver.ResolveAsync(
            CancellationToken.None,
            checks);

        Assert.Equal(DesktopMcpContractKind.Available, result.Kind);
        Assert.NotNull(result.Contract);
        Assert.Equal(pipeName, result.Contract.PipeName);
        Assert.Equal(process.ProcessId, result.Contract.DesktopProcessId);
        Assert.Equal(fixture.PluginDirectory, result.Contract.PluginDirectory);
        Assert.Equal([pipeName], probeCalls);
        Assert.Equal(64, result.Contract.Fingerprint.Length);
        Assert.DoesNotContain(pipeName, result.Detail, StringComparison.Ordinal);

        var launch = DesktopMcpContractResolver.BuildLauncherStartInfo(result.Contract);
        Assert.True(launch.RedirectStandardInput);
        Assert.True(launch.RedirectStandardOutput);
        Assert.True(launch.RedirectStandardError);
        Assert.Equal($@"\\.\pipe\{pipeName}", launch.Environment["CODEX_APP_TOOLS_PIPE_PATH"]);
        Assert.Equal(fixture.NodePath, launch.Environment["CODEX_MCP_NODE_PATH"]);
        Assert.Equal(fixture.ResourcesPath, launch.Environment["CODEX_ELECTRON_RESOURCES_PATH"]);
    }

    [Fact]
    public async Task RejectsAmbiguousVerifiedToolsPipesWithoutChoosingOne()
    {
        var fixture = CreateRuntimeFixture();
        var first = $"codex-browser-use-{Guid.NewGuid():D}";
        var second = $"codex-browser-use-{Guid.NewGuid():D}";
        var process = new DesktopMcpProcessIdentity(
            4321,
            fixture.DesktopExecutable,
            638900000000000000);
        var checks = new DesktopMcpContractChecks(
            () => RunningObservation(process),
            () => RegistrationJson(process.ProcessId, fixture),
            _ => process,
            () => [first, second],
            (_, _, _) => Task.FromResult(true));

        var result = await DesktopMcpContractResolver.ResolveAsync(
            CancellationToken.None,
            checks);

        Assert.Equal(DesktopMcpContractKind.AmbiguousPipe, result.Kind);
        Assert.Null(result.Contract);
        Assert.DoesNotContain(first, result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(second, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsManifestThatChangesTheAllowlistedShellShape()
    {
        var fixture = CreateRuntimeFixture();
        var manifestPath = Path.Combine(fixture.PluginDirectory, "desktop-mcp.json");
        File.WriteAllText(
            manifestPath,
            DesktopManifestJson(["/d", "/s", "/c", "whoami"]));

        Assert.Throws<InvalidDataException>(() =>
            DesktopMcpContractResolver.ReadAndValidateManifest(
                manifestPath,
                fixture.PluginDirectory));
    }

    [Fact]
    public void RejectsManifestThatSelectsAnAlternateCommandPath()
    {
        var fixture = CreateRuntimeFixture();
        var manifestPath = Path.Combine(fixture.PluginDirectory, "desktop-mcp.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["mcpServers"]!["codex_app"]!["command"] = @"C:\alternate\cmd.exe";
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        Assert.Throws<InvalidDataException>(() =>
            DesktopMcpContractResolver.ReadAndValidateManifest(
                manifestPath,
                fixture.PluginDirectory));
    }

    [Fact]
    public void AppServerOverrideUsesStableLauncherAndNeverCapturesDynamicDesktopValues()
    {
        var command = Path.GetFullPath(@"C:\continuity\CodexContinuity.exe");
        var overrideValue = DesktopMcpContractResolver.BuildAppServerOverride(
            command,
            ["mcp-launcher"]);

        Assert.StartsWith("mcp_servers.codex_app={", overrideValue, StringComparison.Ordinal);
        Assert.Contains("mcp-launcher", overrideValue, StringComparison.Ordinal);
        Assert.Contains("automation_update", overrideValue, StringComparison.Ordinal);
        Assert.DoesNotContain("CODEX_APP_TOOLS_PIPE_PATH", overrideValue, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsApps", overrideValue, StringComparison.OrdinalIgnoreCase);

        var startInfo = Program.CreateAppServerStartInfo(
            @"C:\codex\codex.exe",
            45124,
            command,
            ["mcp-launcher"]);
        Assert.False(startInfo.Environment.ContainsKey("CODEX_APP_TOOLS_PIPE_PATH"));
        Assert.False(startInfo.Environment.ContainsKey("CODEX_MCP_NODE_PATH"));
        Assert.False(startInfo.Environment.ContainsKey("CODEX_CLI_PATH"));
        Assert.Contains(overrideValue, startInfo.ArgumentList);
        Assert.Equal(1, startInfo.ArgumentList.Count(argument =>
            argument.StartsWith("mcp_servers.codex_app=", StringComparison.Ordinal)));
    }

    [Fact]
    public void RuntimeRegistrationParserIgnoresMalformedHistoricalEntries()
    {
        var json =
            """
            {
              "schemaVersion": 2,
              "entries": [
                {"schemaVersion":2,"presence":{"pid":0},"paths":{}},
                {"schemaVersion":2,"presence":{"pid":42},"paths":{"resourcesPath":"C:\\pkg\\resources","nodePath":"C:\\runtime\\node.exe","codexCliPath":"C:\\codex.exe"}}
              ]
            }
            """;

        var registrations = DesktopMcpContractResolver.ParseRuntimeRegistrations(json);

        var registration = Assert.Single(registrations);
        Assert.Equal(42, registration.DesktopProcessId);
        Assert.Equal(@"C:\pkg\resources", registration.ResourcesPath);
        Assert.Equal(@"C:\runtime\node.exe", registration.NodePath);
    }

    private RuntimeFixture CreateRuntimeFixture()
    {
        var appDirectory = Path.Combine(
            root,
            "WindowsApps",
            "OpenAI.Codex_1.2.3.0_x64__test",
            "app");
        var resourcesPath = Path.Combine(appDirectory, "resources");
        var pluginDirectory = Path.Combine(
            resourcesPath,
            "plugins",
            "openai-bundled",
            "plugins",
            "codex-app-tools");
        Directory.CreateDirectory(Path.Combine(pluginDirectory, "scripts"));
        File.WriteAllText(
            Path.Combine(pluginDirectory, "scripts", "launch_codex_app_tools_mcp.cmd"),
            "@echo off");
        File.WriteAllText(Path.Combine(pluginDirectory, "server.mjs"), "// fixture");
        File.WriteAllText(
            Path.Combine(pluginDirectory, "desktop-mcp.json"),
            DesktopManifestJson(
            [
                "/d",
                "/s",
                "/c",
                "call",
                "./scripts/launch_codex_app_tools_mcp.cmd",
                "./server.mjs",
            ]));
        Directory.CreateDirectory(runtimeRoot);
        var nodePath = Path.Combine(runtimeRoot, "node.exe");
        File.WriteAllText(nodePath, "fixture");
        return new(
            Path.Combine(appDirectory, "ChatGPT.exe"),
            resourcesPath,
            pluginDirectory,
            nodePath);
    }

    private static CodexDesktopObservation RunningObservation(
        DesktopMcpProcessIdentity process) => new(
        CodexDesktopObservationKind.Running,
        [new CodexDesktopProcessIdentity(process.ProcessId, process.StartedAtUtcTicks)],
        "fixture");

    private static string RegistrationJson(int processId, RuntimeFixture fixture) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            entries = new[]
            {
                new
                {
                    schemaVersion = 2,
                    presence = new { pid = processId },
                    paths = new
                    {
                        resourcesPath = fixture.ResourcesPath,
                        nodePath = fixture.NodePath,
                        codexCliPath = Path.Combine(fixture.ResourcesPath, "codex.exe"),
                    },
                },
            },
        });

    private static string DesktopManifestJson(IReadOnlyList<string> arguments) =>
        JsonSerializer.Serialize(new
        {
            mcpServers = new
            {
                codex_app = new
                {
                    command = "cmd.exe",
                    args = arguments,
                    cwd = ".",
                    enabled = true,
                },
            },
        });

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        if (Directory.Exists(runtimeRoot))
        {
            Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    private sealed record RuntimeFixture(
        string DesktopExecutable,
        string ResourcesPath,
        string PluginDirectory,
        string NodePath);
}
