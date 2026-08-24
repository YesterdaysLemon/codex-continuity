using System.ComponentModel;
using System.Diagnostics;

namespace CodexContinuity;

internal enum SupervisorSuccessorRole
{
    Selected,
    Rollback,
}

internal sealed record SupervisorSuccessorRequest(
    string HandoffId,
    SupervisorSuccessorRole Role);

internal enum SupervisorPredecessorState
{
    Running,
    Exited,
    Unknown,
}

internal static class SupervisorSuccessorAdmission
{
    internal static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    internal static SupervisorSuccessorRequest? ParseRequest(string[] args)
    {
        var handoffId = ArgumentValue(args, "--successor-handoff");
        var roleValue = ArgumentValue(args, "--successor-role");
        if (handoffId is null && roleValue is null)
        {
            return null;
        }
        if (handoffId is null || roleValue is null)
        {
            throw new ArgumentException(
                "A successor handoff requires both --successor-handoff and --successor-role.");
        }
        if (!Guid.TryParseExact(handoffId, "N", out var parsedId) ||
            !parsedId.ToString("N").Equals(handoffId, StringComparison.Ordinal))
        {
            throw new ArgumentException("--successor-handoff requires a lowercase N-format GUID.");
        }
        var role = roleValue.ToLowerInvariant() switch
        {
            "selected" => SupervisorSuccessorRole.Selected,
            "rollback" => SupervisorSuccessorRole.Rollback,
            _ => throw new ArgumentException("--successor-role must be selected or rollback."),
        };
        return new(handoffId, role);
    }

    internal static Task<SupervisorSuccessorHandoff> PrepareAsync(
        string stateDirectory,
        SupervisorSuccessorRequest request,
        int publicPort,
        string? codexHome,
        string currentExecutable,
        CancellationToken cancellationToken) => PrepareAsync(
            stateDirectory,
            request,
            publicPort,
            codexHome,
            currentExecutable,
            () => DateTimeOffset.UtcNow,
            InspectPredecessor,
            Task.Delay,
            cancellationToken);

    internal static async Task<SupervisorSuccessorHandoff> PrepareAsync(
        string stateDirectory,
        SupervisorSuccessorRequest request,
        int publicPort,
        string? codexHome,
        string currentExecutable,
        Func<DateTimeOffset> utcNow,
        Func<int, DateTimeOffset, SupervisorPredecessorState> inspectPredecessor,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentExecutable);
        ArgumentNullException.ThrowIfNull(utcNow);
        ArgumentNullException.ThrowIfNull(inspectPredecessor);
        ArgumentNullException.ThrowIfNull(delay);
        LoopbackEndpoint.ValidatePort(publicPort);

        var deadline = utcNow() + MaximumWait;
        var handoff = LoadAndValidate(
            stateDirectory,
            request,
            publicPort,
            codexHome,
            currentExecutable,
            utcNow());
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var predecessor = inspectPredecessor(
                handoff.PreviousSupervisorProcessId,
                handoff.PreviousSupervisorStartedAtUtc);
            if (predecessor == SupervisorPredecessorState.Exited)
            {
                break;
            }
            if (predecessor == SupervisorPredecessorState.Unknown)
            {
                throw new InvalidOperationException(
                    "The predecessor supervisor identity could not be inspected safely.");
            }
            var now = utcNow();
            if (now >= deadline || now >= handoff.ExpiresAtUtc)
            {
                throw new TimeoutException(
                    "The predecessor supervisor did not exit within the bounded handoff window.");
            }
            await delay(PollInterval, cancellationToken);
        }

        return LoadAndValidate(
            stateDirectory,
            request,
            publicPort,
            codexHome,
            currentExecutable,
            utcNow());
    }

    private static SupervisorSuccessorHandoff LoadAndValidate(
        string stateDirectory,
        SupervisorSuccessorRequest request,
        int publicPort,
        string? codexHome,
        string currentExecutable,
        DateTimeOffset now)
    {
        var result = new SupervisorSuccessorHandoffStore(
            ContinuityPaths.SupervisorHandoffFile(stateDirectory)).Load(now);
        if (result.Kind != SupervisorSuccessorHandoffLoadKind.Loaded || result.Handoff is null)
        {
            throw new InvalidDataException(
                $"The supervisor successor handoff is {LoadKindName(result.Kind)}.");
        }
        var handoff = result.Handoff;
        if (!handoff.HandoffId.Equals(request.HandoffId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The supervisor successor handoff ID does not match.");
        }
        if (handoff.PublicPort != publicPort || !SameOptionalPath(handoff.CodexHome, codexHome))
        {
            throw new InvalidDataException(
                "The successor endpoint or CODEX_HOME does not match the handoff.");
        }

        var expected = request.Role == SupervisorSuccessorRole.Selected
            ? handoff.SelectedBuild
            : handoff.RollbackBuild;
        var current = AutomaticUpdateRunner.ResolveBuildIdentity(currentExecutable)
            ?? throw new InvalidDataException("The successor executable identity is unavailable.");
        if (!SamePath(expected.Executable, currentExecutable) ||
            !expected.Version.Equals(current.Version, StringComparison.OrdinalIgnoreCase) ||
            !expected.ExecutableSha256.Equals(
                current.ExecutableSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The running successor executable does not match the selected handoff identity.");
        }

        var lease = new BackendLeaseStore(
            ContinuityPaths.BackendLeaseFile(stateDirectory)).Load();
        if (lease.Kind != BackendLeaseLoadKind.Loaded ||
            lease.Lease is null ||
            lease.Lease != handoff.Backend)
        {
            throw new InvalidDataException(
                "The persisted backend lease changed after the supervisor handoff was written.");
        }
        return handoff;
    }

    internal static SupervisorPredecessorState InspectPredecessor(
        int processId,
        DateTimeOffset expectedStartedAtUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || process.StartTime.ToUniversalTime() != expectedStartedAtUtc)
            {
                return SupervisorPredecessorState.Exited;
            }
            return SupervisorPredecessorState.Running;
        }
        catch (ArgumentException)
        {
            return SupervisorPredecessorState.Exited;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return SupervisorPredecessorState.Unknown;
        }
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (++index >= args.Length)
            {
                throw new ArgumentException($"{name} requires a value.");
            }
            return args[index];
        }
        return null;
    }

    private static string LoadKindName(SupervisorSuccessorHandoffLoadKind kind) => kind switch
    {
        SupervisorSuccessorHandoffLoadKind.Missing => "missing",
        SupervisorSuccessorHandoffLoadKind.Loaded => "loaded",
        SupervisorSuccessorHandoffLoadKind.Invalid => "invalid",
        SupervisorSuccessorHandoffLoadKind.UnsupportedSchema => "unsupported",
        SupervisorSuccessorHandoffLoadKind.Expired => "expired",
        SupervisorSuccessorHandoffLoadKind.Unreadable => "unreadable",
        _ => throw new InvalidOperationException("Unknown supervisor handoff load result."),
    };

    private static bool SameOptionalPath(string? left, string? right) =>
        left is null && right is null ||
        left is not null && right is not null && SamePath(left, right);

    private static bool SamePath(string left, string right) => Path.GetFullPath(left).Equals(
        Path.GetFullPath(right),
        StringComparison.OrdinalIgnoreCase);
}
