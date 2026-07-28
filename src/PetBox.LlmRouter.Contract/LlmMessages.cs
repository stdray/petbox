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

// The OpenAI-compatible `response_format` request knob (spec: memory-autocapture, bug
// facts-extraction-unparseable-batches): the distiller asks for JSON only in prose today, and
// nothing on the wire holds the model to it — one of the ways a batch comes back as free-form
// reasoning instead of parseable output. Null (the default) sends nothing, so an unset
// ResponseFormat is byte-for-byte today's payload. JsonObject asks for "syntactically valid
// JSON, shape unconstrained" (`{"type":"json_object"}`) — the loosest structural constraint
// every OpenAI-compatible upstream in this fleet understands. JsonSchema additionally pins the
// exact top-level shape via a JSON Schema document (`{"type":"json_schema","json_schema":
// {"name":...,"schema":...}}`). The caller picks the dialect per call; this contract does not.
public abstract record LlmResponseFormat
{
	public sealed record JsonObject : LlmResponseFormat
	{
		public static readonly JsonObject Instance = new();
	}

	public sealed record JsonSchema(string Name, object Schema) : LlmResponseFormat;
}

// ResponseFormat is LAST on purpose (same precedent as LlmRoute.EmbedSpaceId, see
// LlmRegistry.cs) — keeps every existing positional-tail `ChatRequest(...)` call
// source-compatible; null means "no response_format in the payload", i.e. unchanged behavior.
public sealed record ChatRequest(
	IReadOnlyList<ChatMessage> Messages,
	string? Tier = null,
	double? Temperature = null,
	int? MaxTokens = null,
	LlmResponseFormat? ResponseFormat = null);
public sealed record ChatResult(string Text, ModelIdentity Model, ServedBy ServedBy);
