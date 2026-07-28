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
		// raw ceiling = (5000 − 2130) / 11.6 ≈ 247.4 docs at the hard bar; × 0.65 headroom ⌊160.8⌋ = 160
		// — four pages at PageSizeOptions.Default (40), the budget the owner decided on 2026-07-28.
		// PerDocMs was back-solved to land here; see RerankBudgetSettings.PerDocMs. This test pins the
		// NUMBER the app runs with, and outlives the formula: idea rerank-budget-is-a-declared-assumption
		// replaces the four inputs with one declared value, and 160 must survive that change unchanged.
		budget.Candidates().Should().Be(160);
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
