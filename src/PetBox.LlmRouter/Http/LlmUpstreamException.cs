namespace PetBox.LlmRouter.Http;

// Internal signal from the upstream OpenAI-compatible client to the router. `Transient`
// means "try the next provider" (connection refused, timeout, 5xx, 429); non-transient
// (4xx other than 429) means a definitive error the router must surface without masking.
public sealed class LlmUpstreamException : Exception
{
	public bool Transient { get; }

	public LlmUpstreamException(bool transient, string message, Exception? inner = null)
		: base(message, inner) => Transient = transient;
}
