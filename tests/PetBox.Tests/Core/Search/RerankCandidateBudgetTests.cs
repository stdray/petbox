using PetBox.Core.Search;

namespace PetBox.Tests.SearchCore;

// The candidate budget is a single DECLARED number (spec: search-rerank-candidate-budget, idea:
// rerank-budget-is-a-declared-assumption) — an assumption, not a value derived from a formula over
// "measured" latency/cost inputs. These tests lock the declared default and the zero/negative
// floor guard; they do NOT test a derivation, because there is none to test.
public sealed class RerankCandidateBudgetTests
{
	[Fact]
	public void Candidates_DefaultsTo160()
	{
		var budget = new RerankCandidateBudget();
		// 160 = four pages at PageSizeOptions.Default (40), the number the owner declared on
		// 2026-07-28. It used to be the OUTPUT of a four-input formula (back-solved to land here,
		// see git history on this file); it is now the input itself, and must survive that collapse
		// unchanged.
		budget.Candidates().Should().Be(160);
	}

	[Fact]
	public void Candidates_NeverBelowOne_EvenWhenOverriddenToZeroOrNegative()
	{
		// Overriding the budget to 0 (or negative) is now TRIVIAL — it is the whole settings value,
		// no contrived combination of inputs required. The floor guard is what stops that from
		// degrading to an empty search: it must floor at 1, never 0 or negative.
		new RerankCandidateBudget { Value = 0 }.Candidates().Should().Be(1);
		new RerankCandidateBudget { Value = -5 }.Candidates().Should().Be(1);
	}
}
