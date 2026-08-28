namespace PetBox.LlmRouter.Http;

// Internal signal from the upstream OpenAI-compatible client to the router. `Transient`
// means "connection refused, timeout, 5xx, 429" (as opposed to a definitive 4xx). The router
// itself no longer treats the two differently for the purpose of walking the chain — BOTH move
// to the next leg (route-chain-aborts-on-size-refusal) — but `Transient` still shapes the
// breaker (only a transient failure counts against an endpoint's circuit) and the exhaustion
// exception's Transient flag.
// `RateLimited` narrows a transient failure to the specific 429 case so the router can classify
// it as its OWN queryable event and reason (spec: search-degraded-provenance) instead of burying
// it in the generic transient bucket.
public sealed class LlmUpstreamException : Exception
{
	public bool Transient { get; }
	public bool RateLimited { get; }

	public LlmUpstreamException(bool transient, string message, Exception? inner = null, bool rateLimited = false)
		: base(message, inner)
	{
		Transient = transient;
		RateLimited = rateLimited;
	}
}
