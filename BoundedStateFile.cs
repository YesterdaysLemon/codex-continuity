namespace CodexContinuity;

internal enum StateFileRecoveryKind
{
    None,
    CanonicalPresent,
    BackupRestored,
    ReplacementPromoted,
}

internal sealed class BoundedStateFile : IDisposable
{
    private readonly FileStream stream;
    private readonly int maximumBytes;

    private BoundedStateFile(FileStream stream, int maximumBytes)
    {
        this.stream = stream;
        this.maximumBytes = maximumBytes;
    }

    internal static BoundedStateFile Open(string path, int maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (!File.Exists(path) &&
            (File.Exists(TemporaryPath(path)) || File.Exists(BackupPath(path))))
        {
            using var recoveryLock = AcquireRecoveryLock(path);
            RecoverInterruptedWrite(path);
        }
        return new(
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete),
            maximumBytes);
    }

    internal ReadOnlyMemory<byte> Read()
    {
        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException();
        }

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException();
        }
        return bytes;
    }

    internal static void WriteAtomically(
        string path,
        byte[] bytes,
        Action<string, string, string?>? replaceFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"State path has no directory: {path}");
        Directory.CreateDirectory(directory);
        using var recoveryLock = AcquireRecoveryLock(path);
        RecoverInterruptedWrite(path);
        var temporaryPath = TemporaryPath(path);
        var backupPath = BackupPath(path);
        var writingPath = WritingPath(path);
        TryDelete(writingPath);
        TryDelete(temporaryPath);
        TryDelete(backupPath);
        try
        {
            using (var writer = new FileStream(
                       writingPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       options: FileOptions.WriteThrough))
            {
                writer.Write(bytes);
                writer.Flush(flushToDisk: true);
            }
            File.Move(writingPath, temporaryPath);
            if (File.Exists(path))
            {
                try
                {
                    (replaceFile ?? File.Replace)(temporaryPath, path, backupPath);
                }
                catch
                {
                    if (RecoverInterruptedWrite(path) !=
                        StateFileRecoveryKind.ReplacementPromoted)
                    {
                        throw;
                    }
                }
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            TryDelete(writingPath);
            if (File.Exists(path))
            {
                TryDelete(temporaryPath);
                TryDelete(backupPath);
            }
        }
    }

    internal static void DeleteAtomically(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"State path has no directory: {path}");
        if (!Directory.Exists(directory))
        {
            return;
        }
        using var recoveryLock = AcquireRecoveryLock(path);
        File.Delete(WritingPath(path));
        File.Delete(TemporaryPath(path));
        File.Delete(BackupPath(path));
        File.Delete(path);
    }

    internal static StateFileRecoveryKind RecoverInterruptedWrite(string path)
    {
        if (File.Exists(path))
        {
            return StateFileRecoveryKind.CanonicalPresent;
        }
        var backupPath = BackupPath(path);
        if (File.Exists(backupPath))
        {
            File.Move(backupPath, path);
            return StateFileRecoveryKind.BackupRestored;
        }
        var temporaryPath = TemporaryPath(path);
        if (File.Exists(temporaryPath))
        {
            File.Move(temporaryPath, path);
            return StateFileRecoveryKind.ReplacementPromoted;
        }
        return StateFileRecoveryKind.None;
    }

    internal static string TemporaryPath(string path) => $"{path}.tmp";

    internal static string BackupPath(string path) => $"{path}.bak";

    internal static string WritingPath(string path) => $"{path}.writing";

    public void Dispose() => stream.Dispose();

    private static FileStream AcquireRecoveryLock(string path) => new(
        $"{path}.lock",
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
