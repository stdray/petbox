namespace PetBox.LlmRouter.Contract;

// Which concrete model produced a result. Vectors are model-specific (model+dim+version+
// normalization), so a caller MUST store this next to any embedding to know what is
// comparable — cosine across different embedders is meaningless (llm-embed-degrade).
public sealed record ModelIdentity(string Model, int? Dim = null, string? Version = null);

// Provenance of who served a call: the endpoint that answered, the upstream model id, how
// many providers were tried before one succeeded, and whether the answer is a degraded
// fallback. Surfaced to consumers and logged for observability (llm-observability).
public sealed record ServedBy(string Endpoint, string UpstreamModel, int AttemptCount, bool Degraded);

// ---- embed ----
public sealed record EmbedRequest(IReadOnlyList<string> Inputs, string? Tier = null);
public sealed record EmbedResult(IReadOnlyList<float[]> Vectors, ModelIdentity Model, ServedBy ServedBy);

// ---- rerank ----
public sealed record RerankRequest(string Query, IReadOnlyList<string> Documents, int? TopN = null, string? Tier = null);
public sealed record RerankHit(int Index, double Score);
public sealed record RerankResult(IReadOnlyList<RerankHit> Hits, ModelIdentity Model, ServedBy ServedBy);

// A rerank over a candidate pool that MAY exceed one provider call, carrying the query-level
// model AFFINITY invariant (spec: search-rerank-single-model): ONE SEARCH QUERY = ONE MODEL FOR
// ALL ITS CHUNKS. `Documents` are split into contiguous chunks of `ChunkSize` and every chunk is
// scored by the SAME model, so the `RerankHit` scores across the whole pool share ONE scale — the
// precondition for merging them or taking a global TopN. `ChunkSize <= 0`, or a pool that fits in
// one chunk, degenerates to a single call (today's single-POST form, unchanged). `TopN` is applied
// AFTER the affine merge across the WHOLE pool — valid precisely because one model scored all of it.
// The Index of every returned hit is the GLOBAL position in `Documents`, not a per-chunk offset.
public sealed record RerankQueryRequest(
	string Query, IReadOnlyList<string> Documents, int ChunkSize, int? TopN = null, string? Tier = null);

// ---- chat / summary ----
public sealed record ChatMessage(string Role, string Content);
public sealed record ChatRequest(
	IReadOnlyList<ChatMessage> Messages,
	string? Tier = null,
	double? Temperature = null,
	int? MaxTokens = null);
public sealed record ChatResult(string Text, ModelIdentity Model, ServedBy ServedBy);
