using YobaBox.Log.Core.Models;

namespace YobaBox.Log.Core.Ingestion;

public interface IIngestionPipeline
{
	ValueTask IngestAsync(string projectKey, IReadOnlyList<LogEntryCandidate> batch, CancellationToken ct);
}
