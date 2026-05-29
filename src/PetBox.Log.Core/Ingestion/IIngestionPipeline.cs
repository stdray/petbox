using PetBox.Log.Core.Models;

namespace PetBox.Log.Core.Ingestion;

public interface IIngestionPipeline
{
	ValueTask IngestAsync(string projectKey, string logName, IReadOnlyList<LogEntryCandidate> batch, CancellationToken ct);
}
