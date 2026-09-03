using Mumblr.Core.Text;

namespace Mumblr.Core.Tests;

public class KeytermPlannerTests
{
    [Fact]
    public void Keeps_priority_order_and_truncates_to_the_limit()
    {
        var terms = Enumerable.Range(1, 60).Select(i => $"term{i}").ToList();

        var planned = KeytermPlanner.Plan(terms, KeytermLimits.Realtime);

        planned.Count.ShouldBe(50);
        planned[0].ShouldBe("term1");
        planned[^1].ShouldBe("term50");
    }

    [Fact]
    public void Drops_terms_longer_than_the_backend_allows()
    {
        var terms = new[] { new string('a', 21), "Aspire" };

        var planned = KeytermPlanner.Plan(terms, KeytermLimits.Realtime);

        planned.ShouldBe(["Aspire"]);
    }

    [Fact]
    public void Batch_allows_longer_terms_than_realtime()
    {
        var term = new string('a', 40);

        KeytermPlanner.Plan([term], KeytermLimits.Batch).ShouldBe([term]);
        KeytermPlanner.Plan([term], KeytermLimits.Realtime).ShouldBeEmpty();
    }

    [Fact]
    public void Trims_blank_entries_and_deduplicates_case_insensitively()
    {
        var planned = KeytermPlanner.Plan(["  Aspire  ", "", "   ", "aspire"], KeytermLimits.Batch);

        planned.ShouldBe(["Aspire"]);
    }

    [Fact]
    public void Batch_caps_at_one_thousand_terms()
    {
        var terms = Enumerable.Range(1, 1200).Select(i => $"t{i}");

        KeytermPlanner.Plan(terms, KeytermLimits.Batch).Count.ShouldBe(1000);
    }
}
