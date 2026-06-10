namespace PetBox.Core.Search;

// A batch of embeddings plus the identity needed to compare them: only vectors from the same
// (Model, Dim) may be cosined together (the model/dim guard). Dim is the produced dimension
// (after any MRL truncation the embedder applies).
public readonly record struct EmbedBatch(IReadOnlyList<float[]> Vectors, string Model, int Dim);

// The narrow embedding capability the search layer needs — declared IN Core.Search so a Class-B
// vector index never drags a dependency on the LLM-router contract into Core (consumer
// decoupling). The wiring layer adapts the real ILlmClient to this at the edge.
public interface IEmbedder
{
	Task<EmbedBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default);
}
