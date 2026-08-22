using System.Buffers.Binary;
using System.IO.Compression;

namespace CodexContinuity;

internal static class ReleaseArchiveExtractor
{
    internal const long MaximumExtractedBytes = 512L * 1024 * 1024;
    internal const int MaximumEntryCount = 1024;
    internal const long MaximumCentralDirectoryBytes = 16L * 1024 * 1024;

    internal static async Task ExtractToDirectoryAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken,
        long maximumExtractedBytes = MaximumExtractedBytes,
        int maximumEntries = MaximumEntryCount,
        long maximumCentralDirectoryBytes = MaximumCentralDirectoryBytes)
    {
        ValidateCentralDirectory(
            archivePath,
            maximumEntries,
            maximumCentralDirectoryBytes);

        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > maximumEntries)
        {
            throw new InvalidDataException(
                $"Release archive contains more than {maximumEntries} entries.");
        }

        Directory.CreateDirectory(destination);
        var destinationRoot = Path.GetFullPath(destination)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        long declaredBytes = 0;
        long extractedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            declaredBytes = checked(declaredBytes + entry.Length);
            if (declaredBytes > maximumExtractedBytes)
            {
                throw new InvalidDataException(
                    $"Release archive expands beyond {maximumExtractedBytes} bytes.");
            }

            var outputPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            if (!outputPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Release archive entry escapes the extraction directory: {entry.FullName}");
            }
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using var source = entry.Open();
            await using var output = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            extractedBytes += await CopyBoundedAsync(
                source,
                output,
                maximumExtractedBytes - extractedBytes,
                cancellationToken);
        }
    }

    internal static async Task<long> CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        var buffer = new byte[81920];
        long copied = 0;
        while (true)
        {
            var remaining = maximumBytes - copied;
            var readLimit = remaining >= buffer.Length
                ? buffer.Length
                : checked((int)remaining + 1);
            var read = await source.ReadAsync(buffer.AsMemory(0, readLimit), cancellationToken);
            if (read == 0)
            {
                return copied;
            }
            copied += read;
            if (copied > maximumBytes)
            {
                throw new InvalidDataException(
                    $"Content exceeds the {maximumBytes}-byte safety limit.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void ValidateCentralDirectory(
        string archivePath,
        int maximumEntries,
        long maximumCentralDirectoryBytes)
    {
        const int eocdLength = 22;
        const uint eocdSignature = 0x06054b50;
        using var stream = File.OpenRead(archivePath);
        if (stream.Length < eocdLength)
        {
            throw new InvalidDataException("Release archive has no ZIP central directory.");
        }

        var tailLength = (int)Math.Min(stream.Length, eocdLength + ushort.MaxValue);
        var tail = new byte[tailLength];
        stream.Seek(-tailLength, SeekOrigin.End);
        stream.ReadExactly(tail);
        for (var index = tail.Length - eocdLength; index >= 0; index--)
        {
            var candidate = tail.AsSpan(index);
            if (BinaryPrimitives.ReadUInt32LittleEndian(candidate) != eocdSignature)
            {
                continue;
            }
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(candidate[20..]);
            if (index + eocdLength + commentLength != tail.Length)
            {
                throw new InvalidDataException("Release archive central directory is malformed.");
            }

            var disk = BinaryPrimitives.ReadUInt16LittleEndian(candidate[4..]);
            var centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(candidate[6..]);
            var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(candidate[8..]);
            var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(candidate[10..]);
            var centralDirectoryBytes = BinaryPrimitives.ReadUInt32LittleEndian(candidate[12..]);
            var centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(candidate[16..]);
            if (disk != 0 || centralDirectoryDisk != 0 || entriesOnDisk != totalEntries)
            {
                throw new InvalidDataException("Multi-disk release archives are not supported.");
            }
            if (totalEntries == ushort.MaxValue ||
                centralDirectoryBytes == uint.MaxValue ||
                centralDirectoryOffset == uint.MaxValue)
            {
                throw new InvalidDataException("ZIP64 release archives are not supported.");
            }
            if (totalEntries > maximumEntries)
            {
                throw new InvalidDataException(
                    $"Release archive contains more than {maximumEntries} entries.");
            }
            var eocdOffset = stream.Length - tail.Length + index;
            if (centralDirectoryOffset > eocdOffset)
            {
                throw new InvalidDataException("Release archive central directory is malformed.");
            }
            var actualCentralDirectoryBytes = eocdOffset - centralDirectoryOffset;
            if (actualCentralDirectoryBytes > maximumCentralDirectoryBytes)
            {
                throw new InvalidDataException(
                    "Release archive central directory exceeds its safety limit.");
            }
            if (actualCentralDirectoryBytes != centralDirectoryBytes)
            {
                throw new InvalidDataException("Release archive central directory is malformed.");
            }
            return;
        }

        throw new InvalidDataException("Release archive has no valid ZIP central directory.");
    }
}
