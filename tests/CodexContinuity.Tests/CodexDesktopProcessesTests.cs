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
}
