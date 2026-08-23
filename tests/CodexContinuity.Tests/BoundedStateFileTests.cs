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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OpenRecoversACompleteInterruptedWrite(bool backupSurvived)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.json");
        var temporaryPath = BoundedStateFile.TemporaryPath(path);
        var backupPath = BoundedStateFile.BackupPath(path);
        var oldBytes = "{\"value\":\"old\"}"u8.ToArray();
        var newBytes = "{\"value\":\"new\"}"u8.ToArray();
        File.WriteAllBytes(temporaryPath, newBytes);
        if (backupSurvived)
        {
            File.WriteAllBytes(backupPath, oldBytes);
        }

        using var recovered = BoundedStateFile.Open(path, maximumBytes: 128);

        Assert.Equal(backupSurvived ? oldBytes : newBytes, recovered.Read().ToArray());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void PromotedReplacementHasSuccessSemanticsWhileRestoredBackupRethrows()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.json");
        var oldBytes = "{\"value\":\"old\"}"u8.ToArray();
        var newBytes = "{\"value\":\"new\"}"u8.ToArray();
        File.WriteAllBytes(path, oldBytes);

        BoundedStateFile.WriteAtomically(path, newBytes, (_, canonical, _) =>
        {
            File.Delete(canonical);
            throw new IOException("injected temp-only replacement failure");
        });
        Assert.Equal(newBytes, File.ReadAllBytes(path));

        Assert.Throws<IOException>(() => BoundedStateFile.WriteAtomically(
            path,
            oldBytes,
            (_, canonical, backup) =>
            {
                File.Move(canonical, backup!);
                throw new IOException("injected backup replacement failure");
            }));
        Assert.Equal(newBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void CrashTruncatedWritingFileIsNeverPromoted()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.json");
        File.WriteAllText(BoundedStateFile.WritingPath(path), "{\"partial\":");

        Assert.Throws<FileNotFoundException>(() =>
            BoundedStateFile.Open(path, maximumBytes: 128));
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(BoundedStateFile.WritingPath(path)));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
