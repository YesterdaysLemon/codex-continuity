using System.Text.Json.Nodes;
using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class RpcReadBudgetTests
{
    [Fact]
    public void ThreadPageParserRejectsMissingOrDisappearingEntries()
    {
        Assert.Throws<InvalidOperationException>(() => Program.RpcClient.ParseThreadData(null));
        Assert.Throws<InvalidOperationException>(() => Program.RpcClient.ParseThreadData(
            JsonNode.Parse("""[null]""")));

        var malformedStatus = Assert.Single(Program.RpcClient.ParseThreadData(JsonNode.Parse(
            """[{"id":"thread-1","status":{"type":12}}]""")));
        Assert.Equal("unknown", malformedStatus.Status);
    }

    [Fact]
    public void PageItemAndCursorBudgetsFailClosed()
    {
        var budget = new RpcReadBudget(maximumItems: 2, maximumPages: 2);
        budget.BeginPage();
        budget.AddItems(2);
        budget.ObserveCursor("next");
        budget.BeginPage();

        Assert.Throws<InvalidOperationException>(() => budget.AddItems(1));
        Assert.Throws<InvalidOperationException>(() => budget.ObserveCursor("next"));
        Assert.Throws<InvalidOperationException>(budget.BeginPage);
    }

    [Theory]
    [InlineData(0L, 4, 4)]
    [InlineData(3L, 1, 4)]
    public void MessageBudgetAcceptsItsBoundary(long current, int appended, int maximum) =>
        RpcReadBudget.EnsureMessageFits(current, appended, maximum);

    [Fact]
    public void MessageBudgetRejectsOversizedInput()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RpcReadBudget.EnsureMessageFits(currentBytes: 4, appendedBytes: 1, maximumBytes: 4));
    }
}
