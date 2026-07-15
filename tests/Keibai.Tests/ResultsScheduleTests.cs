using Keibai.Core.Domain;
using Keibai.Core.Ingestion;
using Xunit;

namespace Keibai.Tests;

public class ResultsScheduleTests
{
    private static readonly DateOnly Today = new(2026, 7, 15);

    [Fact]
    public void Same_court_same_opening_date_collapses_to_one_round()
    {
        var rounds = ResultsHandler.DueRounds(
            [Item("a", Today), Item("b", Today)], Today);

        Assert.Equal([("31111", Today)], rounds);
    }

    [Fact]
    public void Same_court_different_opening_dates_are_two_rounds()
    {
        var yesterday = Today.AddDays(-1);
        var rounds = ResultsHandler.DueRounds(
            [Item("a", Today), Item("b", yesterday)], Today);

        Assert.Equal(2, rounds.Count);
        Assert.Contains(("31111", Today), rounds);
        Assert.Contains(("31111", yesterday), rounds);
    }

    [Fact]
    public void Opening_date_before_the_window_is_excluded()
    {
        var rounds = ResultsHandler.DueRounds([Item("a", Today.AddDays(-5))], Today);

        Assert.Empty(rounds);
    }

    [Fact]
    public void Null_opening_date_is_excluded()
    {
        var rounds = ResultsHandler.DueRounds([Item("a", null)], Today);

        Assert.Empty(rounds);
    }

    private static PropertyItem Item(string saleUnitId, DateOnly? opening) => new()
    {
        Id = $"31111:{saleUnitId}",
        SaleUnitId = saleUnitId,
        CourtId = "31111",
        PrefectureId = "13",
        OpeningDate = opening,
    };
}
