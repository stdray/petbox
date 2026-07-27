using System.Text.Json.Serialization;

namespace PetBox.Core.Search;

// The RANKING axis of a query is a CALLER CHOICE (spec: search-ranking-mode-is-caller-choice), not
// something the pipeline infers from reranker availability. Precision and Speed are both full,
// deliberate asks — the core (SearchService) never guesses between them from _reranker's presence;
// whoever builds the request supplies the mode. The DEFAULT a caller gets when it doesn't specify
// one lives at the EDGE (the MCP verb / UI page that first constructs the request), never here and
// never in the per-family service (TasksService/MemoryService) either — both simply propagate
// whatever SearchRequest.RankingMode carries.
public enum SearchRankingMode
{
	// Attempt the штатный precision path: cross-encoder rerank of the deduped candidate union when a
	// rerank route is live; falls through to the honest RRF degradation on any failure (no route, an
	// outage, a resolver failure). The edge default where a ranking mistake costs more than latency —
	// an agent acting on the result (MCP verbs: tasks_search/memory_search/session_search).
	Precision,

	// Skip the rerank path outright: no reranker is even CONSTRUCTED for this query, and
	// IsRerankAvailableAsync is never called — straight to RRF fusion. Reported honestly as a CHOICE
	// (SearchRankingOutcome.ChosenRrf), never confused with Precision's degradation. The edge default
	// where latency costs more than a ranking mistake — a human skimming a results page (UI search).
	Speed,
}

// Provenance's RANKING outcome (spec: search-rerank-in-loop) — ONE axis, three honestly distinct
// values. Replaces the old boolean `Reranked` on SearchRetrievers, which could only say true/false
// and so could not tell "RRF because the caller chose speed" from "RRF because precision degraded".
// Do not add a second flag beside this one to carry a state it is missing — a state this contract
// needs belongs IN the enum, or the two can silently drift apart.
[JsonConverter(typeof(JsonStringEnumConverter<SearchRankingOutcome>))]
public enum SearchRankingOutcome
{
	// The штатный precision path ran: the cross-encoder rescored the deduped candidate union.
	Reranked,

	// The caller asked for Precision (or left the mode to its edge default) but the rerank path could
	// not run this time — no route, an outage, a resolver failure — so RRF answered as an HONEST
	// DEGRADATION, never a co-equal strategy.
	DegradedRrf,

	// The caller explicitly asked for Speed. RRF answered because that is what was asked for — an
	// honest CHOICE, never dressed up as a degradation.
	ChosenRrf,
}
