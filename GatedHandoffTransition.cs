namespace CodexContinuity;

internal sealed record GatedHandoffDecision(
    ContinuityHandoffPlan Plan,
    RelayGateLease? GateLease);

internal static class GatedHandoffTransition
{
    internal static async Task<GatedHandoffDecision> CloseAndRecomputeAsync(
        LoopbackRelay relay,
        Func<CancellationToken, Task<ContinuityHandoffPlan>> recomputePlan,
        TimeSpan? recomputeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(recomputePlan);
        var effectiveTimeout = recomputeTimeout ?? TimeSpan.FromSeconds(15);
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(recomputeTimeout));
        }

        var keepGateClosed = false;
        RelayGateLease? gateLease = null;
        try
        {
            gateLease = await relay.CloseGateExclusivelyAsync();
            using var timeout = new CancellationTokenSource(effectiveTimeout);
            ContinuityHandoffPlan plan;
            try
            {
                plan = await recomputePlan(timeout.Token).WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Handoff-plan recomputation exceeded {effectiveTimeout}.");
            }

            keepGateClosed = plan.TransitionReady;
            return new GatedHandoffDecision(
                plan,
                keepGateClosed ? gateLease : null);
        }
        finally
        {
            if (!keepGateClosed)
            {
                gateLease?.TryOpen();
            }
        }
    }
}
