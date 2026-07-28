using PetBox.Core.Settings;

namespace PetBox.Core.Search;

// The rerank CANDIDATE BUDGET — how many candidates a search query carries into the cross-encoder
// rerank pass (spec: search-rerank-candidate-budget). It is a single DECLARED number, an
// assumption the owner states, NOT a value derived from a formula over "measured" latency/cost
// inputs.
//
// That formula (LatencyBarMs, PerDocMs, BaseMs, HeadroomFraction) is GONE (owner decision
// 2026-07-28, idea rerank-budget-is-a-declared-assumption): it gave the appearance of a model
// derived from data that the underlying measurements could not support — synthetic documents, a
// seven-fold run-to-run spread, one machine, one day — and when a specific target number was
// wanted instead, the input was back-solved to produce it (commit 24479d59: PerDocMs back-solved
// to 11.6 so the formula would land on 160). Latency belongs to the rerank PROVIDER — a slow
// provider is a reason to accept it or switch providers, not to shrink the search depth.
//
// Overridable System -> Workspace -> Project (spec: settings-uniform-override, deeper wins) via
// PetBox.Core.Settings.RerankBudgetSettings — build one from resolved settings via
// FromSettings(...) below. The default here (160) matches RerankBudgetSettings' own default, so a
// caller that still does `new RerankCandidateBudget()` (nothing resolved) gets the same honest
// number as one that reads settings and finds no override.
//
// This budget is the VECTOR leg's top-K only: the enumerable «лексическая нога» returns everything
// the facet predicate leaves and has NO top-K, so the budget never caps it — a generous top-K on
// the lexical leg is exactly the defect this must not carry.
public sealed record RerankCandidateBudget
{
	// The declared candidate budget. An assumption, not a measurement — see the type-level comment.
	public int Value { get; init; } = 160;

	// Builds a budget from resolved settings (RerankBudgetSettings), so a caller with an
	// ISettingsResolver in hand can honor the owner's override instead of this compiled-in
	// fallback.
	public static RerankCandidateBudget FromSettings(RerankBudgetSettings settings) => new()
	{
		Value = settings.Candidates,
	};

	// THE production door: every SearchService call site resolves its budget through here instead of
	// constructing RerankCandidateBudget directly, so "переопределяемы" (settings-uniform-override)
	// is true in prod, not just in a settings-layer test. `settingsResolver` is nullable for the same
	// reason every other optional collaborator in these services is (ILlmClient?, ILogger?, ...): a
	// hand-constructed test/adapter instance with no DI graph still gets an honest, unwired budget
	// rather than a null-ref. Settings are resolved at Scope.Project — the cascade still reaches
	// Workspace and System for a project with no override of its own (settings-uniform-override,
	// deeper wins) — so a Project-scope override on the search's own project is what actually lands.
	public static async Task<RerankCandidateBudget> ResolveAsync(
		ISettingsResolver? settingsResolver, string projectKey, CancellationToken ct = default)
	{
		if (settingsResolver is null) return new RerankCandidateBudget();
		var settings = await settingsResolver.GetAsync<RerankBudgetSettings>(Scope.Project, projectKey, ct);
		return FromSettings(settings);
	}

	// The declared budget, floored at 1. A 0-or-negative override is now trivial to set (it used to
	// take a contrived combination of the four old inputs to hit this path) — it must degrade to
	// "rank 1 candidate", never to an empty search.
	public int Candidates() => Value < 1 ? 1 : Value;
}
