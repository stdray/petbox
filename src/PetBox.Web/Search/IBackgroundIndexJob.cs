namespace PetBox.Web.Search;

// One module's contribution to the background enrichment tick: enumerate that module's scope
// files and drain each into its background index. The hosted SearchEnrichmentService loops
// every registered job each tick. Most jobs materialize Class-B VECTOR indexes (memory, tasks
// retrofit) via an AsyncVectorizationWorker — but not all: SessionTermIndexJob only
// TOKENIZES (a lexical FTS index, no embedding), and SessionDigestJob distills text. The name
// is therefore "background index job", not "vectorization job" (the old IVectorizationJob
// misnomer). Returns the number of items processed this pass (for logging).
public interface IBackgroundIndexJob
{
	Task<int> DrainAllAsync(CancellationToken ct);
}
