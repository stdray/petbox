namespace PetBox.LlmRouter.Http;

// WHY THERE IS A CLASS AND NOT JUST A BOOL. `Transient` answers "should the chain move on?" and
// `RateLimited` answers "should this be reported as a rate-limit refusal?". Neither answers the
// question the BREAKER has to answer: how widely should this failure suppress the endpoint?
//
//   * Unreachable — no answer came back at all: connect refused, DNS, TLS, timeout. The host is
//     down or asleep, and that is true for EVERY capability pointed at it. The endpoint-wide
//     breaker is exactly right here and is the whole point of llm-fast-down: a sleeping `home` must
//     not cost a connect-timeout on every subsequent call, whichever capability is calling.
//   * Throttled — the provider ANSWERED, with 429. The endpoint is alive; a quota is exhausted.
//     OpenRouter's free tier is a per-endPOINT budget that a Chat 429 exhausts, so an endpoint-wide
//     breaker on a 429 moves Embed and Rerank traffic for a problem that only Chat had.
//   * ServerError — the provider answered 5xx. Also alive; the model or that request failed.
//     Leg-scoped, same as Throttled.
//
// Measured on production 2026-10-05: two Chat 429s on `openrouter` opened the endpoint-wide breaker,
// and Embed + Rerank were served by the `home` fallback for 30 s with nothing in the log to explain
// it. That is the case Throttled fixes; the case Unreachable preserves.
public enum LlmFailureClass
{
	/// <summary>A definitive refusal (4xx, oversize, unparseable body): no breaker at all.</summary>
	None,

	/// <summary>No answer from the host: endpoint-wide breaker.</summary>
	Unreachable,

	/// <summary>The host answered 429: this leg's breaker only.</summary>
	Throttled,

	/// <summary>The host answered 5xx: this leg's breaker only.</summary>
	ServerError,
}

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

	/// <summary>
	/// How wide this failure suppresses the endpoint. Defaults to the conservative reading of
	/// `transient` — a transient failure that names no class is treated as
	/// <see cref="LlmFailureClass.Unreachable"/>, i.e. endpoint-wide, because that is the behaviour
	/// every pre-classification caller relied on and the expensive direction to get wrong for a
	/// sleeping host.
	/// </summary>
	public LlmFailureClass FailureClass { get; }

	public LlmUpstreamException(bool transient, string message, Exception? inner = null, bool rateLimited = false,
		LlmFailureClass? failureClass = null)
		: base(message, inner)
	{
		Transient = transient;
		RateLimited = rateLimited;
		FailureClass = failureClass ?? (!transient ? LlmFailureClass.None
			: rateLimited ? LlmFailureClass.Throttled
			: LlmFailureClass.Unreachable);
	}
}
