using CodexContinuity;
using System.Text;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class BoundedStateFileTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-bounded-state-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ReadSnapshotDeniesInPlaceWritersButPermitsAtomicReplacement()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.json");
        var replacement = Path.Combine(root, "replacement.json");
        var original = "{\"value\":\"original\"}"u8.ToArray();
        var updated = Encoding.UTF8.GetBytes(
            "{\"value\":\"updated\"}" + new string(' ', 1024));
        File.WriteAllBytes(path, original);
        File.WriteAllBytes(replacement, updated);

        using var snapshot = BoundedStateFile.Open(path, maximumBytes: 128);
        Assert.Throws<IOException>(() => new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete));

        File.Replace(replacement, path, destinationBackupFileName: null);

        Assert.Equal(original, snapshot.Read().ToArray());
        Assert.Equal(updated, File.ReadAllBytes(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
