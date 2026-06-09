namespace PetBox.Core.Search;

// Provenance for a search response: which retrievers actually ran and whether the
// result is degraded (e.g. the semantic retriever was requested but embedding was
// unavailable, so only lexical ran). Surfaced so callers can honestly say
// "vector unavailable → only FTS ran" rather than silently returning lexical-only.
public readonly record struct SearchRetrievers(bool Lexical, bool Semantic, bool Degraded);
