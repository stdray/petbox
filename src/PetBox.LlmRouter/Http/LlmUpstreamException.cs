namespace PetBox.LlmRouter.Http;

// Internal signal from the upstream OpenAI-compatible client to the router. `Transient`
// means "try the next provider" (connection refused, timeout, 5xx, 429); non-transient
// (4xx other than 429) means a definitive error the router must surface without masking.
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
