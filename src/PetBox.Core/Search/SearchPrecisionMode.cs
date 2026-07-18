namespace PetBox.Core.Search;

// The search pipeline has TWO modes, named here so the difference is explicit in code rather than
// implied by which branch happened to run (spec: search-rerank-in-loop). BOTH are full systems;
// they are NOT peers. The mode is reported HONESTLY in provenance (SearchRetrievers): the precision
// mode sets Reranked=true, the degraded mode is a Degraded result with Reranked=false — so a caller
// can always tell which one answered, and RRF is never dressed up as an equal alternative.
public enum SearchPrecisionMode
{
	// PRECISION (штатный): each leg produces candidates → union with dedup → a cross-encoder rerank
	// pass rescores the union → top-N. The rerank pass rescores every candidate on ONE model (see
	// the router's query-level affinity), so the final order is one honest scale.
	//
	// NOTE: enabling the rerank pass IN THE LOOP is a DEFERRED slice (search-rerank-in-loop «б»)
	// behind an eval gate — a weak home model must be shown to beat RRF on a real log before
	// "home always preferred / reranker gates the output" is switched on. This enum NAMES the mode;
	// it does not by itself flip that switch.
	Precision,

	// DEGRADED (реранкер недоступен): plain RRF fusion of the legs' ranked lists. This is a HONEST
	// DEGRADATION, not a co-equal ranking strategy — it runs only when the reranker cannot. The
	// gentle per-leg solo-contribution cap (a noise knob for this rare path — NOT a candidate
	// budget) is permitted ONLY here; in the precision mode the reranker owns the ordering and no
	// such cap applies.
	DegradedRrf,
}

// The gentle per-leg solo-contribution cap of the DEGRADED RRF path ONLY (see SearchPrecisionMode).
// It bounds how many results a SINGLE leg may contribute with no corroboration from the other leg,
// to keep the rare fusion-only path from being flooded by one noisy leg. It is a NOISE KNOB of a
// degraded path, deliberately kept distinct from RerankCandidateBudget (which is a latency-derived
// pool size for the precision path) — conflating the two is the category error this type prevents.
// Disabled by default: wiring it into the live fusion changes output and rides with the reranker
// slice + an owner check, not this mechanism-only landing.
public sealed record DegradedSoloContributionCap
{
	public bool Enabled { get; init; }
	// Max solo (single-leg, uncorroborated) contributions kept from ONE leg in the degraded path.
	public int MaxSoloPerLeg { get; init; } = 10;

	// The mode this cap is even allowed to run in — the type refuses to be applied in precision mode.
	public static SearchPrecisionMode AppliesIn => SearchPrecisionMode.DegradedRrf;
}
