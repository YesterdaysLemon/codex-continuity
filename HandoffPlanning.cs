using System.Text.Json.Nodes;
using System.Text.Json;

namespace CodexContinuity;

internal sealed record ThreadLifecycleStatus(
    string Type,
    IReadOnlyList<string> ActiveFlags,
    bool Malformed)
{
    internal static ThreadLifecycleStatus Parse(JsonNode? statusNode)
    {
        if (statusNode is not JsonObject status ||
            status["type"] is not JsonValue typeValue ||
            !typeValue.TryGetValue<string>(out var type) ||
            string.IsNullOrWhiteSpace(type))
        {
            return Unknown();
        }

        if (!type.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            return status.ContainsKey("activeFlags")
                ? new(type, [], Malformed: true)
                : new(type, [], Malformed: false);
        }

        if (status["activeFlags"] is not JsonArray activeFlags)
        {
            return new(type, [], Malformed: true);
        }

        var parsedFlags = new List<string>();
        foreach (var flagNode in activeFlags)
        {
            if (flagNode is not JsonValue flagValue ||
                !flagValue.TryGetValue<string>(out var flag) ||
                string.IsNullOrWhiteSpace(flag))
            {
                return new(type, parsedFlags, Malformed: true);
            }
            parsedFlags.Add(flag);
        }
        return new(type, parsedFlags, Malformed: false);
    }

    internal static ThreadLifecycleStatus Unknown() => new("unknown", [], Malformed: true);
}

internal sealed record HandoffBlockerCounts(
    int Running,
    int WaitingOnApproval,
    int WaitingOnUserInput,
    int SystemError,
    int Unknown);

internal sealed record ContinuityHandoffPlan(
    string Action,
    bool TransitionReady,
    bool BackendReady,
    string UpdateState,
    bool PendingUpdate,
    int ThreadCount,
    HandoffBlockerCounts Blockers,
    IReadOnlyList<string> Reasons);

internal enum ContinuitySelectedBuildLoadKind
{
    Loaded,
    MissingInstallState,
    InactiveInstallState,
    InvalidInstallState,
    UnreadableInstallState,
    UnverifiedExecutable,
}

internal sealed record ContinuitySelectedBuildLoadResult(
    ContinuitySelectedBuildLoadKind Kind,
    ContinuityBuildIdentity? Build);

internal static class ContinuitySelectedBuildReader
{
    internal static ContinuitySelectedBuildLoadResult Load(string stateDirectory)
    {
        try
        {
            var state = new InstallStateStore(
                ContinuityPaths.InstallStateFile(stateDirectory)).Load();
            if (state is null)
            {
                return new(ContinuitySelectedBuildLoadKind.MissingInstallState, Build: null);
            }
            if (state.Lifecycle != InstallLifecycle.Installed)
            {
                return new(ContinuitySelectedBuildLoadKind.InactiveInstallState, Build: null);
            }
            if (!IsSha256(state.BinarySha256))
            {
                return new(ContinuitySelectedBuildLoadKind.InvalidInstallState, Build: null);
            }

            var build = AutomaticUpdateRunner.ResolveBuildIdentity(state.InstalledExecutable);
            return build is not null && string.Equals(
                build.ExecutableSha256,
                state.BinarySha256,
                StringComparison.OrdinalIgnoreCase)
                    ? new(ContinuitySelectedBuildLoadKind.Loaded, build)
                    : new(ContinuitySelectedBuildLoadKind.UnverifiedExecutable, Build: null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or NotSupportedException)
        {
            return new(ContinuitySelectedBuildLoadKind.InvalidInstallState, Build: null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(ContinuitySelectedBuildLoadKind.UnreadableInstallState, Build: null);
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

internal static class ContinuityHandoffPlanner
{
    internal static ContinuityHandoffPlan Create(
        bool backendReady,
        IReadOnlyList<ThreadLifecycleStatus> threads,
        ContinuityUpdateStateLoadResult updateState,
        ContinuitySelectedBuildLoadResult selectedBuild)
    {
        var running = 0;
        var waitingOnApproval = 0;
        var waitingOnUserInput = 0;
        var systemError = 0;
        var unknown = 0;

        foreach (var thread in threads)
        {
            if (thread.Malformed)
            {
                unknown++;
                continue;
            }

            switch (thread.Type.ToLowerInvariant())
            {
                case "notloaded":
                case "idle":
                    break;
                case "systemerror":
                    systemError++;
                    break;
                case "active":
                    if (thread.ActiveFlags.Count == 0)
                    {
                        running++;
                        break;
                    }

                    var recognizedFlag = false;
                    var unknownFlag = false;
                    foreach (var flag in thread.ActiveFlags.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (flag.Equals("waitingOnApproval", StringComparison.OrdinalIgnoreCase))
                        {
                            waitingOnApproval++;
                            recognizedFlag = true;
                        }
                        else if (flag.Equals("waitingOnUserInput", StringComparison.OrdinalIgnoreCase))
                        {
                            waitingOnUserInput++;
                            recognizedFlag = true;
                        }
                        else
                        {
                            unknownFlag = true;
                        }
                    }
                    if (!recognizedFlag || unknownFlag)
                    {
                        unknown++;
                    }
                    break;
                default:
                    unknown++;
                    break;
            }
        }

        var blockers = new HandoffBlockerCounts(
            running,
            waitingOnApproval,
            waitingOnUserInput,
            systemError,
            unknown);
        var reasons = new List<string>();
        if (!backendReady)
        {
            reasons.Add("backendUnavailable");
        }

        var (pendingUpdate, updateReason) = EvaluateUpdateState(updateState, selectedBuild);
        if (updateReason is not null)
        {
            reasons.Add(updateReason);
        }
        if (running > 0)
        {
            reasons.Add("runningTurns");
        }
        if (waitingOnApproval > 0)
        {
            reasons.Add("waitingOnApproval");
        }
        if (waitingOnUserInput > 0)
        {
            reasons.Add("waitingOnUserInput");
        }
        if (systemError > 0)
        {
            reasons.Add("systemError");
        }
        if (unknown > 0)
        {
            reasons.Add("unknownThreadState");
        }

        var transitionReady = reasons.Count == 0;
        return new ContinuityHandoffPlan(
            transitionReady ? pendingUpdate ? "applyUpdate" : "handoff" : "wait",
            transitionReady,
            backendReady,
            updateState.Kind.ToString(),
            pendingUpdate,
            threads.Count,
            blockers,
            reasons);
    }

    private static (bool PendingUpdate, string? Reason) EvaluateUpdateState(
        ContinuityUpdateStateLoadResult loadResult,
        ContinuitySelectedBuildLoadResult selectedBuild)
    {
        if (loadResult.Kind != ContinuityUpdateStateLoadKind.Loaded || loadResult.State is null)
        {
            return (false, $"updateState{loadResult.Kind}");
        }
        if (selectedBuild.Kind != ContinuitySelectedBuildLoadKind.Loaded ||
            selectedBuild.Build is null)
        {
            return (false, $"selectedBuild{selectedBuild.Kind}");
        }

        var state = loadResult.State;
        if (!state.RunningProcessObserved || state.RunningExecutableSha256 is null)
        {
            return (false, "runningUpdateStateUnverified");
        }
        if (state.SelectedVersion.Equals(state.RunningVersion, StringComparison.OrdinalIgnoreCase))
        {
            return selectedBuild.Build.Version.Equals(
                       state.RunningVersion,
                       StringComparison.OrdinalIgnoreCase) &&
                   selectedBuild.Build.ExecutableSha256.Equals(
                       state.RunningExecutableSha256,
                       StringComparison.OrdinalIgnoreCase)
                ? (false, null)
                : (false, "selectedBuildDoesNotMatchUpdateState");
        }

        var selectedRelease = state.Releases.FirstOrDefault(release =>
            release.Version.Equals(state.SelectedVersion, StringComparison.OrdinalIgnoreCase));
        return selectedRelease is
        {
            StagedAtUtc: not null,
            StagedExecutableSha256: { } selectedSha256,
        } &&
        selectedBuild.Build.Version.Equals(
            state.SelectedVersion,
            StringComparison.OrdinalIgnoreCase) &&
        selectedBuild.Build.ExecutableSha256.Equals(
            selectedSha256,
            StringComparison.OrdinalIgnoreCase)
            ? (true, null)
            : (false, "selectedUpdateUnverified");
    }
}
