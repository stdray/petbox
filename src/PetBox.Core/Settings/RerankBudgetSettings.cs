namespace PetBox.Core.Settings;

// The four inputs to the rerank CANDIDATE BUDGET (PetBox.Core.Search.RerankCandidateBudget),
// moved out of that record's hardcoded `init` defaults (bug: rerank-budget-params-to-settings).
// Before this, `SearchService.cs` always did `budget ?? new RerankCandidateBudget()` — there was
// nowhere to override the constants, so the owner could not correct a bad calibration or move the
// latency bar without a rebuild. Spec search-rerank-candidate-budget requires the budget be DERIVED
// from the latency bar and the MEASURED per-document cost, never from a code constant; spec
// settings-uniform-override requires it be overridable at any scope from System down to Project
// (deeper wins). Read the whole group with:
//
//   resolver.GetAsync<RerankBudgetSettings>(Scope.Project, projectKey)
//
// then hand the result to RerankCandidateBudget.FromSettings(...).
//
// DEFAULTS ARE A MEASURED ESTIMATE WITH KNOWN ERROR, NOT MEASURED TRUTH (2026-07-27, endpoint-
// concurrency-limit, artifact:measurement). The measurement used SYNTHETIC documents, showed a
// SEVEN-FOLD run-to-run spread, and ran on ONE machine — it is the reason these constants had to
// stop living in code, not a replacement fact to trust at face value. Re-measure before leaning on
// these numbers for anything precise; they are deliberately picked pessimistic (see PerDocMs) so
// the failure mode of being wrong is "fewer candidates than necessary", not "the 5s bar breaks again".
public sealed record RerankBudgetSettings
{
	[Setting(TopLevel = Scope.System, Key = "search.rerank.budget.latencyBarMs",
		Description = "Latency bar (ms) the rerank candidate budget is derived from — the owner's ceiling for the whole search response.")]
	public double LatencyBarMs { get; init; } = 5000;

	// Open question from the task card, resolved HERE: kept as a SCALAR (option a), not a function of
	// the pool's average candidate length (option b). Reason: SearchService.RankPoolAsync computes the
	// budget (poolLimit = _budget.Candidates()) BEFORE it truncates the fused pool and BEFORE any
	// candidate text is resolved — resolveCandidateText only runs on the already-truncated `pool`
	// (see SearchService.cs, RankPoolAsync: poolLimit is read at the top, texts are resolved several
	// lines later against `pool`). A length-based function would need to know candidate length to
	// decide how many candidates to keep, but the pipeline does not have that length until AFTER the
	// budget already gated which candidates get resolved — a chicken-and-egg the current architecture
	// does not support without an extra, currently-nonexistent pre-fetch. So: a single scalar, taken at
	// the UPPER end of the measured 80-1700 byte range (6.3 / 8.4 / 16.8 ms/doc) rather than the middle
	// or the old 6.1 constant — the budget is knowingly PESSIMISTIC (fewer candidates than a
	// length-aware estimate might allow), which fails safe against the 5s bar rather than failing open.
	[Setting(TopLevel = Scope.System, Key = "search.rerank.budget.perDocMs",
		Description = "Per-document marginal rerank cost (ms), taken at the upper end of the measured range on purpose (pessimistic scalar, not a length function — see code comment for why).")]
	public double PerDocMs { get; init; } = 16.8;

	[Setting(TopLevel = Scope.System, Key = "search.rerank.budget.baseMs",
		Description = "Fixed per-call base cost of the rerank route (ms) — a rough, high-variance measured estimate, not measured truth.")]
	public double BaseMs { get; init; } = 2130;

	[Setting(TopLevel = Scope.System, Key = "search.rerank.budget.headroomFraction",
		Description = "Fraction of the raw latency ceiling kept as budget, so warm p95 (not just the min) stays under the bar.")]
	public double HeadroomFraction { get; init; } = 0.65;
}
