using PetBox.Core.Settings;

namespace PetBox.Core.Search;

// The rerank CANDIDATE BUDGET — how many candidates a search query is allowed to carry into a
// (future) cross-encoder rerank pass (spec: search-rerank-candidate-budget). Its whole point is
// that the number is DERIVED from the latency bar and the MEASURED per-document cost of the real
// rerank route — NOT a relevance intuition, NOT a "generous top-K" constant picked by feel.
//
// The four inputs below used to be the ONLY source of truth (record `init` defaults), and nothing
// could override them in production (bug: rerank-budget-params-to-settings — SearchService.cs did
// `budget ?? new RerankCandidateBudget()` unconditionally, so a bad calibration or a new latency
// bar needed a rebuild to fix). They now ALSO live in PetBox.Core.Settings.RerankBudgetSettings,
// overridable System -> Workspace -> Project (spec: settings-uniform-override, deeper wins) — build
// one from resolved settings via FromSettings(...) below. The defaults here are kept identical to
// RerankBudgetSettings' own defaults, so a caller that still does `new RerankCandidateBudget()`
// (nothing resolved) gets the same honest number as one that reads settings and finds no override.
//
// 2026-07-18 measurement (warm, home route qwen3-rerank-0.6b, whole list one POST) produced the
// ORIGINAL BaseMs=350 / PerDocMs=6.1 pair — SUPERSEDED, see below.
//
// 2026-07-27 re-measurement (endpoint-concurrency-limit, artifact:measurement) found the 2026-07-18
// pair badly wrong and is why these constants moved to settings instead of being hand-corrected in
// place: BaseMs measured ~2130ms (six times the old 350), and PerDocMs turned out NOT to be one
// number — 6.3 / 8.4 / 16.8 ms/doc were observed at 80 / 400 / 1700-byte documents, i.e. cost scales
// with document length. THIS MEASUREMENT ITSELF IS A ROUGH ESTIMATE, NOT GROUND TRUTH: the
// documents were synthetic, the run-to-run spread was seven-fold, and it ran on one machine — that
// unreliability is the reason the numbers now live in overridable settings rather than being
// re-baked as new hardcoded constants. Given that, the honest candidate budget the card measured
// was 111-222 candidates (vs. 495 out of the old, wrong constants) — the 5s bar does NOT hold at
// 495 on long documents.
//
// Open question resolved (task rerank-budget-params-to-settings): PerDocMs stays a SCALAR, not a
// function of the pool's average candidate length, because SearchService.RankPoolAsync computes
// poolLimit = _budget.Candidates() BEFORE it truncates the fused pool and BEFORE any candidate text
// is resolved (resolveCandidateText only runs against the already-truncated `pool`) — a
// length-based function would need to know candidate length to size the budget, but the pipeline
// does not have that length until AFTER the budget already gated which candidates get resolved. So:
// PerDocMs is taken at the UPPER end of the measured range (16.8, not the old 6.1 or the range's
// middle) — a deliberately PESSIMISTIC scalar, which lands the default budget at the 111 end (fails
// safe against the bar) rather than guessing at the 222 end (fails open if the guess is wrong).
//
// This budget is the VECTOR leg's top-K only: the «лексическая нога» is enumerable (it returns
// everything the facet predicate leaves, it has NO top-K), so the budget never caps it — a generous
// top-K on the lexical leg is exactly the defect this must not carry.
public sealed record RerankCandidateBudget
{
	// The owner's latency bar for the whole search response (spec decision: 5 seconds).
	public double LatencyBarMs { get; init; } = 5000;

	// Per-document marginal cost and fixed per-call base of the real rerank route (warm).
	// MEASURED ESTIMATE WITH KNOWN ERROR, not measured truth — see the type-level comment above
	// before treating these as more precise than "roughly right, safe direction".
	// 11.6 is BACK-SOLVED so this formula yields 160 — four pages at PageSizeOptions.Default — which
	// is the budget the owner decided on (2026-07-28). It is a knob, not a measured per-document cost.
	// RerankBudgetSettings.PerDocMs carries the full reasoning, including why the measured numbers
	// here are not load-bearing and why this four-input shape is being replaced by ONE declared
	// number (idea rerank-budget-is-a-declared-assumption).
	public double PerDocMs { get; init; } = 11.6;
	public double BaseMs { get; init; } = 2130;

	// Fraction of the raw latency ceiling kept as budget, so warm p95 (not just the min) stays
	// under the bar. A policy choice, not a measurement — unaffected by the PerDocMs/BaseMs re-measure.
	public double HeadroomFraction { get; init; } = 0.65;

	// Builds a budget from resolved settings (RerankBudgetSettings), so a caller with an
	// ISettingsResolver in hand can honor the owner's override instead of these compiled-in
	// fallbacks.
	public static RerankCandidateBudget FromSettings(RerankBudgetSettings settings) => new()
	{
		LatencyBarMs = settings.LatencyBarMs,
		PerDocMs = settings.PerDocMs,
		BaseMs = settings.BaseMs,
		HeadroomFraction = settings.HeadroomFraction,
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

	// The derived budget: how many candidates fit under the latency bar, with headroom. Never a
	// stored constant — recompute it whenever the route or the bar changes and re-measure PerDocMs.
	public int Candidates()
	{
		var rawCeiling = (LatencyBarMs - BaseMs) / PerDocMs;   // docs that fit at the hard bar
		var budget = (int)System.Math.Floor(rawCeiling * HeadroomFraction);
		return budget < 1 ? 1 : budget;
	}
}
