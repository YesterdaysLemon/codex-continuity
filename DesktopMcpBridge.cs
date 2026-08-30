using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace CodexContinuity;

internal enum DesktopMcpContractKind
{
    Available,
    DesktopNotRunning,
    UnsafeDesktopObservation,
    RegistrationUnavailable,
    AmbiguousRegistration,
    RuntimeInvalid,
    PipeUnavailable,
    AmbiguousPipe,
}

internal sealed record DesktopMcpProcessIdentity(
    int ProcessId,
    string ExecutablePath,
    long StartedAtUtcTicks);

internal sealed record DesktopMcpRuntimeRegistration(
    int DesktopProcessId,
    string ResourcesPath,
    string NodePath,
    string? CodexCliPath);

internal sealed record DesktopMcpLaunchManifest(
    string Command,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

internal sealed record DesktopMcpContract(
    int DesktopProcessId,
    long DesktopStartedAtUtcTicks,
    string PipeName,
    string ResourcesPath,
    string NodePath,
    string? CodexCliPath,
    string PluginDirectory,
    DesktopMcpLaunchManifest LaunchManifest,
    string Fingerprint);

internal sealed record DesktopMcpContractResult(
    DesktopMcpContractKind Kind,
    DesktopMcpContract? Contract,
    string Detail)
{
    internal bool IsAvailable => Kind == DesktopMcpContractKind.Available && Contract is not null;
}

internal sealed record DesktopMcpContractChecks(
    Func<CodexDesktopObservation> ObserveDesktop,
    Func<string?> ReadRuntimeRegistration,
    Func<int, DesktopMcpProcessIdentity?> InspectProcess,
    Func<IReadOnlyList<string>> EnumeratePipeNames,
    Func<string, int, CancellationToken, Task<bool>> ProbePipe)
{
    internal static DesktopMcpContractChecks Native { get; } = new(
        CodexDesktopProcesses.Capture,
        DesktopMcpContractResolver.ReadNativeRegistration,
        DesktopMcpContractResolver.InspectNativeProcess,
        DesktopMcpContractResolver.EnumerateNativePipeNames,
        DesktopMcpContractResolver.ProbeNativePipeAsync);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(ObserveDesktop);
        ArgumentNullException.ThrowIfNull(ReadRuntimeRegistration);
        ArgumentNullException.ThrowIfNull(InspectProcess);
        ArgumentNullException.ThrowIfNull(EnumeratePipeNames);
        ArgumentNullException.ThrowIfNull(ProbePipe);
    }
}

internal static class DesktopMcpContractResolver
{
    internal const int BridgeVersion = 1;
    private const int MaximumRegistrationBytes = 512 * 1024;
    private const int MaximumRegistrationEntries = 64;
    private const int MaximumPipeCandidates = 128;
    private const int MaximumPipeResponseBytes = 1024 * 1024;
    private const string PipePrefix = "codex-browser-use-";
    private const string PluginRelativePath =
        @"plugins\openai-bundled\plugins\codex-app-tools";
    private const string ManifestFileName = "desktop-mcp.json";
    private static readonly string RuntimeRegistrationPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenAI",
        "Codex",
        "chrome-native-hosts-v2.json");

    internal static async Task<DesktopMcpContractResult> ResolveAsync(
        CancellationToken cancellationToken,
        DesktopMcpContractChecks? checks = null)
    {
        checks ??= DesktopMcpContractChecks.Native;
        checks.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var observation = checks.ObserveDesktop();
        if (observation.Kind == CodexDesktopObservationKind.NotRunning)
        {
            return Result(
                DesktopMcpContractKind.DesktopNotRunning,
                "Codex Desktop is closed; its app-tools capability is intentionally unavailable.");
        }
        if (observation.Kind != CodexDesktopObservationKind.Running)
        {
            return Result(
                DesktopMcpContractKind.UnsafeDesktopObservation,
                "The running Codex Desktop processes could not be identified safely.");
        }

        IReadOnlyList<DesktopMcpRuntimeRegistration> registrations;
        try
        {
            registrations = ParseRuntimeRegistrations(checks.ReadRuntimeRegistration());
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or ArgumentException or
                InvalidOperationException)
        {
            return Result(
                DesktopMcpContractKind.RegistrationUnavailable,
                $"The Codex Desktop runtime registration is invalid: {exception.Message}");
        }

        var observedById = observation.Processes.ToDictionary(process => process.ProcessId);
        var candidates = new List<(
            DesktopMcpRuntimeRegistration Registration,
            DesktopMcpProcessIdentity Process,
            string PluginDirectory,
            DesktopMcpLaunchManifest Manifest)>();
        foreach (var registration in registrations.Where(registration =>
                     observedById.ContainsKey(registration.DesktopProcessId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observed = observedById[registration.DesktopProcessId];
            var process = checks.InspectProcess(registration.DesktopProcessId);
            if (process is null || process.StartedAtUtcTicks != observed.StartedAtUtcTicks)
            {
                continue;
            }
            if (!TryValidateRuntime(
                    registration,
                    process,
                    out var pluginDirectory,
                    out var manifest))
            {
                continue;
            }
            candidates.Add((registration, process, pluginDirectory!, manifest!));
        }

        if (candidates.Count == 0)
        {
            return Result(
                DesktopMcpContractKind.RuntimeInvalid,
                "No live Store Codex process matched a complete, package-bound app-tools runtime.");
        }
        if (candidates.Count != 1)
        {
            return Result(
                DesktopMcpContractKind.AmbiguousRegistration,
                "More than one live Store Codex process matched an app-tools runtime; Continuity will not guess.");
        }

        IReadOnlyList<string> pipeNames;
        try
        {
            pipeNames = checks.EnumeratePipeNames()
                .Where(IsCanonicalPipeName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumPipeCandidates + 1)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Result(
                DesktopMcpContractKind.PipeUnavailable,
                $"The local app-tools capability could not be enumerated: {exception.Message}");
        }
        if (pipeNames.Count > MaximumPipeCandidates)
        {
            return Result(
                DesktopMcpContractKind.AmbiguousPipe,
                "Too many local app-tools capability candidates were present; Continuity will not probe them.");
        }

        var candidate = candidates[0];
        var matchedPipes = new List<string>(capacity: 2);
        foreach (var candidatePipeName in pipeNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool matched;
            try
            {
                matched = await checks.ProbePipe(
                    candidatePipeName,
                    candidate.Process.ProcessId,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    TimeoutException or InvalidDataException or JsonException)
            {
                matched = false;
            }
            if (matched)
            {
                matchedPipes.Add(candidatePipeName);
                if (matchedPipes.Count > 1)
                {
                    break;
                }
            }
        }

        if (matchedPipes.Count == 0)
        {
            return Result(
                DesktopMcpContractKind.PipeUnavailable,
                "The live Store Codex process has not published a verifiable app-tools capability yet.");
        }
        if (matchedPipes.Count != 1)
        {
            return Result(
                DesktopMcpContractKind.AmbiguousPipe,
                "More than one verified app-tools capability matched the live Store Codex process.");
        }

        var recheckedProcess = checks.InspectProcess(candidate.Process.ProcessId);
        if (recheckedProcess is null ||
            recheckedProcess.StartedAtUtcTicks != candidate.Process.StartedAtUtcTicks ||
            !Path.GetFullPath(recheckedProcess.ExecutablePath).Equals(
                Path.GetFullPath(candidate.Process.ExecutablePath),
                StringComparison.OrdinalIgnoreCase))
        {
            return Result(
                DesktopMcpContractKind.RuntimeInvalid,
                "Codex Desktop changed while its app-tools capability was being verified.");
        }

        var pipeName = matchedPipes[0];
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n',
                candidate.Process.ProcessId,
                candidate.Process.StartedAtUtcTicks,
                pipeName,
                candidate.Registration.ResourcesPath,
                candidate.Registration.NodePath,
                candidate.PluginDirectory,
                candidate.Manifest.Command,
                string.Join('\u001f', candidate.Manifest.Arguments))))).ToLowerInvariant();
        return new(
            DesktopMcpContractKind.Available,
            new DesktopMcpContract(
                candidate.Process.ProcessId,
                candidate.Process.StartedAtUtcTicks,
                pipeName,
                candidate.Registration.ResourcesPath,
                candidate.Registration.NodePath,
                candidate.Registration.CodexCliPath,
                candidate.PluginDirectory,
                candidate.Manifest,
                fingerprint),
            "A unique app-tools capability was verified against the live Store Codex process.");
    }

    internal static IReadOnlyList<DesktopMcpRuntimeRegistration> ParseRuntimeRegistrations(
        string? json)
    {
        if (string.IsNullOrWhiteSpace(json) ||
            Encoding.UTF8.GetByteCount(json) > MaximumRegistrationBytes)
        {
            throw new InvalidDataException("Runtime registration is missing or exceeds its size limit.");
        }
        var root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidDataException("Runtime registration has no JSON object.");
        if (root["schemaVersion"]?.GetValue<int>() != 2 ||
            root["entries"] is not JsonArray entries ||
            entries.Count > MaximumRegistrationEntries)
        {
            throw new InvalidDataException("Runtime registration schema or entry count is unsupported.");
        }

        var registrations = new List<DesktopMcpRuntimeRegistration>(entries.Count);
        foreach (var node in entries)
        {
            if (node is not JsonObject entry ||
                entry["schemaVersion"]?.GetValue<int>() != 2 ||
                entry["presence"] is not JsonObject presence ||
                presence["pid"] is not JsonValue pidValue ||
                !pidValue.TryGetValue<int>(out var processId) ||
                processId <= 0 ||
                entry["paths"] is not JsonObject paths ||
                !TryBoundedString(paths["resourcesPath"], out var resourcesPath) ||
                !TryBoundedString(paths["nodePath"], out var nodePath))
            {
                continue;
            }
            TryBoundedString(paths["codexCliPath"], out var codexCliPath);
            registrations.Add(new(
                processId,
                resourcesPath!,
                nodePath!,
                codexCliPath));
        }
        return registrations;
    }

    internal static bool TryValidateRuntime(
        DesktopMcpRuntimeRegistration registration,
        DesktopMcpProcessIdentity process,
        out string? pluginDirectory,
        out DesktopMcpLaunchManifest? manifest)
    {
        pluginDirectory = null;
        manifest = null;
        try
        {
            var processPath = Path.GetFullPath(process.ExecutablePath);
            var desktopDirectory = Path.GetDirectoryName(processPath);
            if (desktopDirectory is null ||
                !Path.GetFileName(processPath).Equals(
                    "ChatGPT.exe",
                    StringComparison.OrdinalIgnoreCase) ||
                !processPath.Contains(
                    @"\WindowsApps\OpenAI.Codex_",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var expectedResources = Path.GetFullPath(Path.Combine(desktopDirectory, "resources"));
            var resources = Path.GetFullPath(registration.ResourcesPath);
            if (!resources.Equals(expectedResources, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var localAppData = Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            var expectedRuntimeRoot = Path.GetFullPath(Path.Combine(
                localAppData,
                "OpenAI",
                "Codex",
                "runtimes",
                "cua_node")) + Path.DirectorySeparatorChar;
            var node = Path.GetFullPath(registration.NodePath);
            if (!node.StartsWith(expectedRuntimeRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(node).Equals("node.exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(node))
            {
                return false;
            }

            pluginDirectory = Path.GetFullPath(Path.Combine(resources, PluginRelativePath));
            var resourcesPrefix = resources + Path.DirectorySeparatorChar;
            if (!pluginDirectory.StartsWith(resourcesPrefix, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(pluginDirectory))
            {
                return false;
            }
            manifest = ReadAndValidateManifest(
                Path.Combine(pluginDirectory, ManifestFileName),
                pluginDirectory);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or InvalidOperationException or ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            pluginDirectory = null;
            manifest = null;
            return false;
        }
    }

    internal static DesktopMcpLaunchManifest ReadAndValidateManifest(
        string manifestPath,
        string pluginDirectory)
    {
        using var stateFile = BoundedStateFile.Open(manifestPath, 64 * 1024);
        var document = JsonNode.Parse(stateFile.Read().Span)?.AsObject()
            ?? throw new InvalidDataException("Desktop app-tools manifest is invalid.");
        var root = document["mcpServers"]?["codex_app"]?.AsObject()
            ?? throw new InvalidDataException("Desktop app-tools manifest has no codex_app server.");
        if (root["enabled"]?.GetValue<bool>() != true ||
            !TryBoundedString(root["command"], out var command) ||
            !command!.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
            root["args"] is not JsonArray argumentNodes ||
            argumentNodes.Count is < 1 or > 16 ||
            !TryBoundedString(root["cwd"], out var configuredWorkingDirectory))
        {
            throw new InvalidDataException("Desktop app-tools launch shape is unsupported.");
        }

        var arguments = new List<string>(argumentNodes.Count);
        foreach (var argumentNode in argumentNodes)
        {
            if (!TryBoundedString(argumentNode, out var argument))
            {
                throw new InvalidDataException("Desktop app-tools launch argument is invalid.");
            }
            arguments.Add(argument!);
        }
        string[] requiredArguments =
        [
            "/d",
            "/s",
            "/c",
            "call",
            "./scripts/launch_codex_app_tools_mcp.cmd",
            "./server.mjs",
        ];
        if (!arguments.SequenceEqual(requiredArguments, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Desktop app-tools launch arguments are unsupported.");
        }

        var workingDirectory = Path.GetFullPath(Path.Combine(
            pluginDirectory,
            configuredWorkingDirectory!));
        var pluginRoot = Path.GetFullPath(pluginDirectory);
        if (!workingDirectory.Equals(pluginRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Desktop app-tools working directory escapes its package.");
        }
        var script = Path.Combine(
            pluginRoot,
            "scripts",
            "launch_codex_app_tools_mcp.cmd");
        var server = Path.Combine(pluginRoot, "server.mjs");
        if (!File.Exists(script) || !File.Exists(server))
        {
            throw new InvalidDataException("Desktop app-tools package is incomplete.");
        }
        return new(command!, arguments, workingDirectory);
    }

    internal static ProcessStartInfo BuildLauncherStartInfo(DesktopMcpContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var startInfo = new ProcessStartInfo(contract.LaunchManifest.Command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = contract.LaunchManifest.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in contract.LaunchManifest.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["CODEX_APP_TOOLS_PIPE_PATH"] = $@"\\.\pipe\{contract.PipeName}";
        startInfo.Environment["CODEX_MCP_NODE_PATH"] = contract.NodePath;
        startInfo.Environment["CODEX_BROWSER_USE_NODE_PATH"] = contract.NodePath;
        startInfo.Environment["CODEX_ELECTRON_RESOURCES_PATH"] = contract.ResourcesPath;
        startInfo.Environment.Remove("CODEX_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(contract.CodexCliPath))
        {
            startInfo.Environment["CODEX_CLI_PATH"] = contract.CodexCliPath;
        }
        return startInfo;
    }

    internal static async Task<int> RunLauncherAsync(CancellationToken cancellationToken)
    {
        var discovery = await ResolveAsync(cancellationToken);
        if (!discovery.IsAvailable)
        {
            Console.Error.WriteLine($"Codex app tools unavailable: {discovery.Detail}");
            return 2;
        }

        using var process = Process.Start(BuildLauncherStartInfo(discovery.Contract!))
            ?? throw new InvalidOperationException("Could not start the verified Codex app-tools bridge.");
        using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var input = PumpLauncherInputAsync(
            Console.OpenStandardInput(),
            process.StandardInput,
            inputCancellation.Token);
        var output = process.StandardOutput.BaseStream.CopyToAsync(
            Console.OpenStandardOutput(),
            cancellationToken);
        var error = process.StandardError.BaseStream.CopyToAsync(
            Console.OpenStandardError(),
            cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            inputCancellation.Cancel();
            await AwaitLauncherPumpsAsync(input, output, error, inputCancellation.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            inputCancellation.Cancel();
            await AwaitLauncherPumpsAsync(input, output, error, inputCancellation.Token);
            throw;
        }
    }

    private static async Task PumpLauncherInputAsync(
        Stream source,
        StreamWriter destination,
        CancellationToken cancellationToken)
    {
        try
        {
            await source.CopyToAsync(destination.BaseStream, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
        finally
        {
            destination.Close();
        }
    }

    private static async Task AwaitLauncherPumpsAsync(
        Task input,
        Task output,
        Task error,
        CancellationToken inputCancellationToken)
    {
        try
        {
            await input;
        }
        catch (OperationCanceledException) when (inputCancellationToken.IsCancellationRequested)
        {
        }
        await Task.WhenAll(output, error);
    }

    internal static string BuildAppServerOverride(
        string command,
        IReadOnlyList<string> launcherArguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(launcherArguments);
        if (!Path.IsPathFullyQualified(command) || launcherArguments.Count is < 1 or > 4)
        {
            throw new InvalidDataException("Continuity app-tools launcher identity is invalid.");
        }
        var arguments = string.Join(",", launcherArguments.Select(argument =>
            $"\"{EscapeTomlString(argument)}\""));
        return "mcp_servers.codex_app={" +
            $"\"command\"=\"{EscapeTomlString(command)}\"," +
            $"\"args\"=[{arguments}]," +
            "\"enabled\"=true," +
            "\"default_tools_approval_mode\"=\"approve\"," +
            "\"tools\"={" +
                "\"automation_update\"={\"approval_mode\"=\"prompt\"}," +
                "\"create_thread\"={\"approval_mode\"=\"prompt\"}," +
                "\"send_message_to_thread\"={\"approval_mode\"=\"prompt\"}," +
                "\"fork_thread\"={\"approval_mode\"=\"prompt\"}," +
                "\"handoff_thread\"={\"approval_mode\"=\"prompt\"}}," +
            "\"startup_timeout_sec\"=15," +
            "\"tool_timeout_sec\"=3600," +
            "\"omit_tools_from\"=[\"deferred\"]}";
    }

    internal static (string Command, IReadOnlyList<string> Arguments) SelfInvocation(
        string processPath,
        string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        var fullProcessPath = Path.GetFullPath(processPath);
        if (Path.GetFileNameWithoutExtension(fullProcessPath).Equals(
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
            return (fullProcessPath, [Path.GetFullPath(assemblyPath), "mcp-launcher"]);
        }
        return (fullProcessPath, ["mcp-launcher"]);
    }

    internal static string? ReadNativeRegistration()
    {
        if (!File.Exists(RuntimeRegistrationPath))
        {
            return null;
        }
        using var stateFile = BoundedStateFile.Open(
            RuntimeRegistrationPath,
            MaximumRegistrationBytes);
        return Encoding.UTF8.GetString(stateFile.Read().Span);
    }

    internal static DesktopMcpProcessIdentity? InspectNativeProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || process.MainModule?.FileName is not { } executablePath)
            {
                return null;
            }
            return new(
                processId,
                executablePath,
                process.StartTime.ToUniversalTime().Ticks);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
                NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static IReadOnlyList<string> EnumerateNativePipeNames() =>
        Directory.EnumerateFiles(@"\\.\pipe\", $"{PipePrefix}*")
            .Select(path => path.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)
                ? path[@"\\.\pipe\".Length..]
                : Path.GetFileName(path))
            .ToArray();

    internal static async Task<bool> ProbeNativePipeAsync(
        string pipeName,
        int expectedDesktopProcessId,
        CancellationToken cancellationToken)
    {
        if (!IsCanonicalPipeName(pipeName) || expectedDesktopProcessId <= 0)
        {
            return false;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var ownerProcessId) ||
                ownerProcessId != (uint)expectedDesktopProcessId)
            {
                return false;
            }

            var request = Encoding.UTF8.GetBytes(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{\"threadStartKind\":\"all\"}}");
            var header = BitConverter.GetBytes(request.Length);
            await pipe.WriteAsync(header, timeout.Token);
            await pipe.WriteAsync(request, timeout.Token);
            await pipe.FlushAsync(timeout.Token);

            await ReadExactlyAsync(pipe, header, timeout.Token);
            var responseLength = BitConverter.ToInt32(header);
            if (responseLength is < 2 or > MaximumPipeResponseBytes)
            {
                throw new InvalidDataException(
                    "App-tools capability returned an invalid frame length.");
            }
            var responseBytes = new byte[responseLength];
            await ReadExactlyAsync(pipe, responseBytes, timeout.Token);
            var response = JsonNode.Parse(responseBytes)?.AsObject();
            if (response?["id"] is not JsonValue idValue ||
                !idValue.TryGetValue<int>(out var responseId) ||
                responseId != 1 ||
                response["result"] is not JsonObject result ||
                result["tools"] is not JsonArray tools ||
                tools.Count == 0)
            {
                return false;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in tools)
            {
                if (tool is JsonObject toolObject &&
                    toolObject["name"] is JsonValue nameValue &&
                    nameValue.TryGetValue<string>(out var name) &&
                    !string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
            return names.Contains("create_thread") &&
                names.Contains("send_message_to_thread") &&
                names.Contains("automation_update");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("App-tools capability closed an incomplete frame.");
            }
            offset += read;
        }
    }

    private static bool TryBoundedString(JsonNode? node, out string? value)
    {
        value = null;
        if (node is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<string>(out var candidate) ||
            string.IsNullOrWhiteSpace(candidate) ||
            candidate.Length > 32767 ||
            candidate.Any(char.IsControl))
        {
            return false;
        }
        value = candidate;
        return true;
    }

    private static bool IsCanonicalPipeName(string pipeName) =>
        pipeName.Length == PipePrefix.Length + 36 &&
        pipeName.StartsWith(PipePrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(pipeName[PipePrefix.Length..], "D", out _);

    private static string EscapeTomlString(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    private static DesktopMcpContractResult Result(
        DesktopMcpContractKind kind,
        string detail) => new(kind, Contract: null, detail);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);
}
