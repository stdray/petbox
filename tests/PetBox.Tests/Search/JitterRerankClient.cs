using PetBox.LlmRouter.Contract;
using PetBox.Tests.Memory;

namespace PetBox.Tests.Search;

// THE MEASURED SHAPE (a), as small as it can be made: the same documents, in the same order, every
// call — with the scores moving in their low digits from one call to the next. That is what the
// local route does on short documents and what the cloud route does on every set it was measured
// on, and it is the shape a memory/tasks pool cannot survive, because the score it stores IS this
// number. The step is deliberately far below the gap between neighbours (0.01), so nothing can
// reorder: anything this makes fail is caused by the SCORE alone.
public sealed class JitterRerankClient : ILlmClient
{
	readonly FakeLlmClient _inner = new();
	int _calls;

	// Takes the cross-encoder away without touching the embedder — the honest RRF degradation, not
	// a whole-route outage.
	public bool RerankDown { get; set; }

	public int RerankCalls => Volatile.Read(ref _calls);

	public Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default) =>
		_inner.EmbedAsync(projectKey, request, ct);

	public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default)
	{
		if (RerankDown) throw new InvalidOperationException("rerank route down");
		var n = Interlocked.Increment(ref _calls);
		// 2.6441e-4 is the grid step actually observed between two identical passes on the live
		// route — small enough to be invisible to a reader, large enough that a hash of the score
		// is a different hash.
		var jitter = n % 2 == 0 ? 2.6441e-4 : 0.0;
		IReadOnlyList<RerankHit> hits =
		[
			.. request.Documents
				.Select((_, i) => new RerankHit(i, 1.0 - 0.01 * i + jitter))
				.Take(request.TopN ?? request.Documents.Count),
		];
		return Task.FromResult(new RerankResult(hits, new ModelIdentity("jitter-rerank", 0),
			new ServedBy("fake", "jitter-rerank", 1, Degraded: false)));
	}

	public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
		throw new NotSupportedException();

	public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
		Task.FromResult(!(RerankDown && capability == LlmCapability.Rerank));
}
