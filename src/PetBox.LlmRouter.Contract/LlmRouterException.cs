namespace PetBox.LlmRouter.Contract;

// Thrown when a capability cannot be served — the chain walked every leg (llm-fallback-chain)
// and none served the call; it never aborts early on a non-transient leg failure any more
// (route-chain-aborts-on-size-refusal). Two cases, read from the AGGREGATE of every leg's
// outcome, not from "which reason ended the walk" (nothing does — the walk always runs to the
// end):
//   Transient == true  -> every leg that actually failed did so transiently (refused/timeout/
//                         5xx/429); the caller may degrade/retry, it may well fix itself.
//   Transient == false -> at least one leg returned a definitive non-transient error (e.g.
//                         400/401/422) — config-level, will not self-heal by retrying alone.
// The Message/InnerException (an AggregateException when there were upstream errors) carry what
// happened on EACH leg, not just the last.
public sealed class LlmRouterException : Exception
{
	public LlmCapability Capability { get; }
	public bool Transient { get; }

	// The capability has NO route configured for this project at all — a structural config hole,
	// not a provider failure. Distinguished because a consumer must be able to say so out loud
	// (search reports it as degradedReason "embed-no-route"): retrying will never fix it.
	public bool NoRoute { get; }

	// The chain exhausted on a RATE LIMIT (HTTP 429) — a route that EXISTS but is throttled, not a
	// config hole and not a generic blip. Carried out so a consumer can report the distinct
	// degradedReason "embed-rate-limited" (spec: search-degraded-provenance). Only meaningful when
	// Transient is true (a 429 is a transient failure); false otherwise.
	public bool RateLimited { get; }

	public LlmRouterException(LlmCapability capability, bool transient, string message, Exception? inner = null,
		bool noRoute = false, bool rateLimited = false)
		: base(message, inner)
	{
		Capability = capability;
		Transient = transient;
		NoRoute = noRoute;
		RateLimited = rateLimited;
	}
}
