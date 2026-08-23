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

    public void Dispose() => stream.Dispose();
}
