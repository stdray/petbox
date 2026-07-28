namespace PetBox.Core.Settings;

// The rerank CANDIDATE BUDGET (PetBox.Core.Search.RerankCandidateBudget) is a single DECLARED
// number — an assumption about how deep ranking should look, never a value derived from a formula
// over "measured" latency/cost inputs (spec: search-rerank-candidate-budget, idea:
// rerank-budget-is-a-declared-assumption, owner decision 2026-07-28).
//
// This record used to carry FOUR inputs (LatencyBarMs, PerDocMs, BaseMs, HeadroomFraction) that fed
// a formula in RerankCandidateBudget.Candidates(). That shape is GONE. It gave the appearance of a
// model derived from measurement while the underlying numbers could not carry that weight: the
// 2026-07-27 re-measurement (synthetic documents, seven-fold run-to-run spread, one machine) found
// the original BaseMs/PerDocMs pair wrong six-fold, and when a specific target (160, four pages at
// PageSizeOptions.Default) was wanted, commit 24479d59 back-solved PerDocMs to 11.6 to land there —
// the target chose the input, not the other way round. Latency is a property of the rerank
// PROVIDER, not a reason to change search depth: a slow provider is an argument to accept it or
// switch providers, never to recompute this number.
//
// Overridable System -> Workspace -> Project (spec: settings-uniform-override, deeper wins). Read via:
//
//   resolver.GetAsync<RerankBudgetSettings>(Scope.Project, projectKey)
//
// then hand the result to RerankCandidateBudget.FromSettings(...).
public sealed record RerankBudgetSettings
{
	[Setting(TopLevel = Scope.System, Key = "search.rerank.budget.candidates",
		Description = "How many fused-pool candidates reach the reranker. A declared assumption, not a value derived from measurement — override it directly if 160 is wrong for this project.")]
	public int Candidates { get; init; } = 160;
}
