using YobaBox.Log.Core.Data;

namespace YobaBox.Log.Core.Ingestion;

public interface ITailBroadcaster
{
	IAsyncEnumerable<LogEntryRecord> Subscribe(string projectKey, CancellationToken ct);
	void Publish(string projectKey, IReadOnlyList<LogEntryRecord> batch);
}
