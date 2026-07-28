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
	// does not support without an extra, currently-nonexistent pre-fetch. So: a single scalar.
	//
	// WHY 11.6 — AND WHY YOU SHOULD NOT READ IT AS A MEASUREMENT (owner's decision 2026-07-28).
	//
	// THE BUDGET IS 160 BY DECISION. 160 = four pages at PageSizeOptions.Default (40). With the bar
	// (5000), BaseMs (2130) and HeadroomFraction (0.65) held fixed, landing on 160 pins PerDocMs at
	// ~11.66 — so the TARGET CHOSE THE INPUT, not the other way round. This value is a back-solved
	// knob, not a per-document cost anyone measured.
	//
	// That inversion is the whole problem, and the owner has called it: the four-input formula gives
	// the APPEARANCE of a model derived from data while the data underneath (synthetic documents,
	// seven-fold run-to-run spread, one machine, one day) cannot carry it. The measured numbers are
	// NOT load-bearing and must not be treated as such — the old 350/6.1 pair was wrong six-fold on
	// BaseMs and survived for months precisely because "derived from a measurement" sounds like a
	// guarantee.
	//
	// THIS SHAPE IS ON ITS WAY OUT. Idea rerank-budget-is-a-declared-assumption (in review) replaces
	// the four inputs with ONE declared number, and rewrites spec search-rerank-candidate-budget,
	// which currently REQUIRES the latency derivation and is the only reason this formula still
	// exists. Latency belongs to the provider: a slow home is a reason to accept it or switch
	// providers, not to recompute search depth. If you are here to change the budget, change it
	// through settings and do not add a fifth input.
	[Setting(TopLevel = Scope.System, Key = "search.rerank.budget.perDocMs",
		Description = "Per-document marginal rerank cost (ms). Mid-range of the measured 6.3-16.8 span; derives a 160-candidate budget (four default pages). Raise it to narrow the budget, lower it to widen.")]
	public double PerDocMs { get; init; } = 11.6;

	[Setting(TopLevel = Scope.System, Key = "search.rerank.budget.baseMs",
		Description = "Fixed per-call base cost of the rerank route (ms) — a rough, high-variance measured estimate, not measured truth.")]
	public double BaseMs { get; init; } = 2130;

	[Setting(TopLevel = Scope.System, Key = "search.rerank.budget.headroomFraction",
		Description = "Fraction of the raw latency ceiling kept as budget, so warm p95 (not just the min) stays under the bar.")]
	public double HeadroomFraction { get; init; } = 0.65;
}
