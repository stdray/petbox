namespace PetBox.LlmRouter.Contract;

// Thrown when a capability cannot be served. Two cases:
//   Transient == true  -> every provider in the chain failed transiently (refused/timeout/
//                         5xx/429) and nothing succeeded; the caller may degrade/retry.
//   Transient == false -> a provider returned a definitive non-transient error (e.g.
//                         400/401/422) that MUST NOT be masked by fallback — surfaced as-is
//                         (llm-fallback-chain).
public sealed class LlmRouterException : Exception
{
	public LlmCapability Capability { get; }
	public bool Transient { get; }

	// The capability has NO route configured for this project at all — a structural config hole,
	// not a provider failure. Distinguished because a consumer must be able to say so out loud
	// (search reports it as degradedReason "embed-no-route"): retrying will never fix it.
	public bool NoRoute { get; }

	public LlmRouterException(LlmCapability capability, bool transient, string message, Exception? inner = null, bool noRoute = false)
		: base(message, inner)
	{
		Capability = capability;
		Transient = transient;
		NoRoute = noRoute;
	}
}
