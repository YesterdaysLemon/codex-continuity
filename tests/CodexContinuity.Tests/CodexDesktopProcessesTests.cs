using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class CodexDesktopProcessesTests
{
    [Fact]
    public void EvaluateCapturesOnlyStoreCodexProcesses()
    {
        var observation = CodexDesktopProcesses.Evaluate([
            new(12, @"C:\Program Files\WindowsApps\OpenAI.Codex_26.818.4152.0_x64__id\app\ChatGPT.exe", 1200),
            new(13, @"C:\Program Files\ChatGPT\ChatGPT.exe", 1300),
        ]);

        Assert.Equal(CodexDesktopObservationKind.Running, observation.Kind);
        Assert.Equal([new CodexDesktopProcessIdentity(12, 1200)], observation.Processes);
        Assert.Equal(
            "Observed 1 process(es) from the running Microsoft Store Codex desktop.",
            observation.Detail);
    }

    [Fact]
    public void EvaluateFailsClosedWhenAChatGptProcessCannotBeInspected()
    {
        var observation = CodexDesktopProcesses.Evaluate([
            new(12, ExecutablePath: null, StartedAtUtcTicks: null),
        ]);

        Assert.Equal(CodexDesktopObservationKind.Unsafe, observation.Kind);
        Assert.Empty(observation.Processes);
    }

    [Fact]
    public async Task WaitForExitWaitsThroughUnknownStateAndPidReuseIsAlreadyExited()
    {
        var states = new Queue<ObservedProcessState>([
            ObservedProcessState.Running,
            ObservedProcessState.Unknown,
            ObservedProcessState.Exited,
        ]);
        var identity = new CodexDesktopProcessIdentity(12, 1200);

        await CodexDesktopProcesses.WaitForExitAsync(
            [identity],
            CancellationToken.None,
            _ => states.Dequeue(),
            TimeSpan.FromMilliseconds(1));

        Assert.Empty(states);
    }

    [Fact]
    public void WaitArgumentsRoundTripDistinctProcessIdentities()
    {
        CodexDesktopProcessIdentity[] processes = [
            new(12, 1200),
            new(13, 1300),
            new(12, 1200),
        ];

        var arguments = CodexDesktopProcesses.BuildWaitArguments(processes);
        var parsed = CodexDesktopProcesses.ParseWaitArguments(arguments.ToArray());

        Assert.Equal(
            [new CodexDesktopProcessIdentity(12, 1200), new CodexDesktopProcessIdentity(13, 1300)],
            parsed);
    }

    [Fact]
    public async Task NaturalClosureRequiresAnEmptyDesktopIntervalAfterOldIdentitiesExit()
    {
        var observations = new Queue<CodexDesktopObservation>([
            new(
                CodexDesktopObservationKind.Running,
                [new CodexDesktopProcessIdentity(13, 1300)],
                "A newly observed desktop is still running."),
            new(
                CodexDesktopObservationKind.Unsafe,
                [],
                "Desktop inspection is temporarily unavailable."),
            new(
                CodexDesktopObservationKind.NotRunning,
                [],
                "No desktop remains."),
        ]);

        await CodexDesktopProcesses.WaitForNaturalClosureAsync(
            [new CodexDesktopProcessIdentity(12, 1200)],
            CancellationToken.None,
            inspect: _ => ObservedProcessState.Exited,
            observe: () => observations.Dequeue(),
            pollInterval: TimeSpan.FromMilliseconds(1));

        Assert.Empty(observations);
    }

    [Fact]
    public async Task VerifiedNewDesktopConnectionBreaksFastRelaunchWaitCycle()
    {
        var observations = 0;

        await CodexDesktopProcesses.WaitForNaturalClosureAsync(
            [new CodexDesktopProcessIdentity(12, 1200)],
            CancellationToken.None,
            verifiedRetargetConnection: Task.CompletedTask,
            inspect: _ => ObservedProcessState.Exited,
            observe: () =>
            {
                observations++;
                return new(
                    CodexDesktopObservationKind.Running,
                    [new CodexDesktopProcessIdentity(13, 1300)],
                    "A new desktop connected to the reserved Continuity endpoint.");
            },
            pollInterval: TimeSpan.FromMilliseconds(1));

        Assert.Equal(1, observations);
    }

    [Theory]
    [InlineData("0:1200")]
    [InlineData("12:0")]
    [InlineData("12")]
    [InlineData("process:time")]
    public void WaitArgumentsRejectInvalidIdentity(string identity) =>
        Assert.Throws<ArgumentException>(() => CodexDesktopProcesses.ParseWaitArguments(
            [CodexDesktopProcesses.WaitArgument, identity]));
}
