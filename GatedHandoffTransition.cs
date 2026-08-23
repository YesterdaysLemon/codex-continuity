namespace CodexContinuity;

internal sealed record GatedHandoffDecision(
    ContinuityHandoffPlan Plan,
    RelayGateLease? GateLease);

internal static class GatedHandoffTransition
{
    private static readonly TimeSpan CancellationDrainTimeout = TimeSpan.FromSeconds(1);

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
            var recomputation = recomputePlan(timeout.Token);
            ContinuityHandoffPlan plan;
            try
            {
                plan = await recomputation.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                await Task.WhenAny(
                    recomputation,
                    Task.Delay(CancellationDrainTimeout));
                if (!recomputation.IsCompleted)
                {
                    keepGateClosed = true;
                    throw new TimeoutException(
                        $"Handoff-plan recomputation did not stop within " +
                        $"{CancellationDrainTimeout} after cancellation. The relay remains gated.");
                }

                _ = recomputation.Exception;
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
