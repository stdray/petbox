namespace PetBox.Sessions.Contract;

// The episodic (stage-2, in-session) search knob — aimed at ONE session's message stream.
//
// The failure it guards: a query with few lexical matches inside a session leaves the
// semantic leg free to fill the hitsPerSession quota with trivial service messages
// ("Записано.", "No response requested.", "```"). A cosine/RRF score floor is the WRONG tool
// for this (and is rejected outright — spec: search-leg-classification — as a membership
// threshold the vector leg must not have): such a message wins rank 0 of the semantic leg, so
// its fused RRF score (1/60 ≈ 0.0167) is the MAXIMUM a single-leg hit can reach, above any sane
// floor. The right guard is a CONTENT-LENGTH exclusion, not a threshold:
//   MinSemanticChars — the junk never ENTERS the semantic candidate set: a message whose
//     trimmed content is shorter than this is not embedded (no wasted embed call / cache row)
//     and is filtered from the cosine candidates. The LEXICAL leg keeps indexing it — a BM25
//     token match on a short message is legitimate and precise, never dropped.
// At 0 it disables (0 chars = embed everything). Substantive vector-only hits enter as peers.
public sealed record SessionEpisodicOptions
{
	public int MinSemanticChars { get; init; } = 30;
}
