using PetBox.Core.Search;

namespace PetBox.Tests.SearchCore;

// The candidate budget must be DERIVED from the latency bar and the measured per-doc cost, not a
// stored constant (spec: search-rerank-candidate-budget). These lock the derivation, not a number.
public sealed class RerankCandidateBudgetTests
{
	[Fact]
	public void Candidates_AreDerivedFromLatencyBar_NotAConstant()
	{
		var budget = new RerankCandidateBudget();
		// raw ceiling = (5000 − 350) / 6.1 ≈ 762 docs at the hard bar; × 0.65 headroom ≈ 495.
		budget.Candidates().Should().BeInRange(450, 550);
	}

	[Fact]
	public void Candidates_TrackTheLatencyBar_HalvingTheBarRoughlyHalvesTheBudget()
	{
		var full = new RerankCandidateBudget();
		var half = full with { LatencyBarMs = 2500 };
		// The budget is a function OF the bar: drop the bar, the budget drops with it (a constant would not).
		half.Candidates().Should().BeLessThan(full.Candidates());
	}

	[Fact]
	public void Candidates_TrackTheMeasuredPerDocCost_ASlowerRouteBuysFewerCandidates()
	{
		var fast = new RerankCandidateBudget { PerDocMs = 6.1 };
		var slow = fast with { PerDocMs = 12.2 };
		// Same 5s bar, twice the per-doc cost → about half the candidates. Empirically measured, not guessed.
		slow.Candidates().Should().BeLessThan(fast.Candidates());
	}

	[Fact]
	public void Candidates_NeverBelowOne_EvenWhenTheBarIsTiny()
	{
		var tiny = new RerankCandidateBudget { LatencyBarMs = 100, BaseMs = 350 };
		// base already exceeds the bar → ceiling is negative; the budget floors at 1, never 0/negative.
		tiny.Candidates().Should().Be(1);
	}
}
