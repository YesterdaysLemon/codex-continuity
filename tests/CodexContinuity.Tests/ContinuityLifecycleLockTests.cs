using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class ContinuityLifecycleLockTests
{
    [Fact]
    public void LifecycleMutationsAreExclusive()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"codex-continuity-lifecycle-lock-tests-{Guid.NewGuid():N}");
        try
        {
            using var first = ContinuityLifecycleLock.Acquire(root);

            var exception = Assert.Throws<InvalidOperationException>(() =>
                ContinuityLifecycleLock.Acquire(root, TimeSpan.Zero));

            Assert.Contains("lifecycle change is already in progress", exception.Message);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
