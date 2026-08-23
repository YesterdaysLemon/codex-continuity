namespace CodexContinuity;

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

    internal static void WriteAtomically(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"State path has no directory: {path}");
        Directory.CreateDirectory(directory);
        var suffix = Guid.NewGuid().ToString("N");
        var temporaryPath = $"{path}.tmp-{suffix}";
        var backupPath = $"{path}.bak-{suffix}";
        var temporaryComplete = false;
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            temporaryComplete = true;
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temporaryPath, path, backupPath);
                }
                catch
                {
                    RecoverFailedReplace(path, temporaryPath, backupPath);
                    throw;
                }
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (!temporaryComplete || File.Exists(path))
            {
                TryDelete(temporaryPath);
            }
            if (File.Exists(path))
            {
                TryDelete(backupPath);
            }
        }
    }

    internal static void RecoverFailedReplace(
        string path,
        string temporaryPath,
        string backupPath)
    {
        if (File.Exists(path))
        {
            return;
        }
        if (File.Exists(backupPath))
        {
            File.Move(backupPath, path);
            return;
        }
        if (File.Exists(temporaryPath))
        {
            File.Move(temporaryPath, path);
        }
    }

    public void Dispose() => stream.Dispose();

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
