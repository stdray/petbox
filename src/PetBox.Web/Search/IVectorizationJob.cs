namespace PetBox.Web.Search;

// One module's contribution to background vectorization: enumerate that module's scope files and
// drain each into its Class-B vector index via an AsyncVectorizationWorker. The hosted
// SearchVectorizationService loops every registered job each tick. Memory registers one; the tasks
// retrofit adds another. Returns the number of docs indexed this pass (for logging).
public interface IVectorizationJob
{
	Task<int> DrainAllAsync(CancellationToken ct);
}
