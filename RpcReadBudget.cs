namespace CodexContinuity;

internal sealed class RpcReadBudget
{
    private readonly int maximumItems;
    private readonly int maximumPages;
    private readonly HashSet<string> seenCursors = new(StringComparer.Ordinal);
    private int itemCount;
    private int pageCount;

    internal RpcReadBudget(int maximumItems, int maximumPages)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPages, 1);
        this.maximumItems = maximumItems;
        this.maximumPages = maximumPages;
    }

    internal void BeginPage()
    {
        pageCount++;
        if (pageCount > maximumPages)
        {
            throw new InvalidOperationException(
                $"thread/list exceeded the {maximumPages} page safety limit.");
        }
    }

    internal void AddItems(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > maximumItems - itemCount)
        {
            throw new InvalidOperationException(
                $"thread/list exceeded the {maximumItems} item safety limit.");
        }
        itemCount += count;
    }

    internal void ObserveCursor(string? cursor)
    {
        if (cursor is not null &&
            (string.IsNullOrWhiteSpace(cursor) || !seenCursors.Add(cursor)))
        {
            throw new InvalidOperationException(
                "thread/list returned an empty or repeated continuation cursor.");
        }
    }

    internal static void EnsureMessageFits(
        long currentBytes,
        int appendedBytes,
        int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(appendedBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        if (currentBytes > maximumBytes - appendedBytes)
        {
            throw new InvalidOperationException(
                $"App-server message exceeded the {maximumBytes} byte safety limit.");
        }
    }
}
