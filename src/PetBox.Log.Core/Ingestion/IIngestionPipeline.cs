using PetBox.Log.Core.Models;

namespace PetBox.Log.Core.Ingestion;

public interface IIngestionPipeline
{
	ValueTask IngestAsync(string projectKey, IReadOnlyList<LogEntryCandidate> batch, CancellationToken ct);
}
