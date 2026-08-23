namespace CodexContinuity;

internal sealed record GatedHandoffDecision(
    ContinuityHandoffPlan Plan,
    RelayGateLease? GateLease);

internal static class GatedHandoffTransition
{
    internal static async Task<GatedHandoffDecision> CloseAndRecomputeAsync(
        LoopbackRelay relay,
        Func<Task<ContinuityHandoffPlan>> recomputePlan,
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
            var plan = await recomputePlan().WaitAsync(effectiveTimeout);
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
