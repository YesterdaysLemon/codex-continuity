namespace CodexContinuity;

internal static class GatedHandoffTransition
{
    internal static async Task<ContinuityHandoffPlan> CloseAndRecomputeAsync(
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
        long? ownedGateEpoch = null;
        try
        {
            ownedGateEpoch = await relay.CloseGateExclusivelyAsync();
            var plan = await recomputePlan().WaitAsync(effectiveTimeout);
            keepGateClosed = plan.TransitionReady;
            return plan;
        }
        finally
        {
            if (!keepGateClosed && ownedGateEpoch is { } epoch)
            {
                relay.TryOpenGate(epoch);
            }
        }
    }
}
