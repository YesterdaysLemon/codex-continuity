using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class CodexExecutableLocatorTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-executable-locator-{Guid.NewGuid():N}");

    [Fact]
    public void RelativePathCandidateIsReturnedAsAbsolute()
    {
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "codex.exe");
        File.WriteAllText(executable, string.Empty);
        var relativeCandidate = Path.GetRelativePath(Environment.CurrentDirectory, executable);

        var selected = Program.SelectCodexExecutable([relativeCandidate]);

        Assert.NotNull(selected);
        Assert.Equal(executable, selected);
        Assert.True(Path.IsPathFullyQualified(selected));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
