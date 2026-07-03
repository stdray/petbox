namespace PetBox.Sessions.Contract;

// The episodic (stage-2, in-session) search knobs — the same W5 fair-fusion recipe stage-1
// discovery got (spec: search-fair-fusion), but aimed at ONE session's message stream.
//
// The failure they guard: a query with few lexical matches inside a session leaves the
// semantic leg free to fill the hitsPerSession quota with trivial service messages
// ("Записано.", "No response requested.", "```"). A score floor ALONE cannot catch these —
// such a message wins rank 0 of the semantic leg, so its fused RRF score (1/60 ≈ 0.0167) is
// the MAXIMUM a single-leg hit can reach, above any sane floor. So it takes two layers:
//   MinSemanticChars — the junk never ENTERS the semantic candidate set: a message whose
//     trimmed content is shorter than this is not embedded (no wasted embed call / cache row)
//     and is filtered from the cosine candidates. The LEXICAL leg keeps indexing it — a BM25
//     token match on a short message is legitimate and precise, never dropped.
//   SemanticFloor — the minimum RAW fused RRF relevance a SEMANTIC-ONLY hit must clear to
//     survive; only trims the weak deep tail (a lexically-confirmed hit is never floored).
//     Same 0.013 rationale as stage-1 discovery.
// Either at 0 disables that layer (0 chars = embed everything; 0 floor = keep the whole tail).
public sealed record SessionEpisodicOptions
{
	public int MinSemanticChars { get; init; } = 30;
	public double SemanticFloor { get; init; } = 0.013;
}
