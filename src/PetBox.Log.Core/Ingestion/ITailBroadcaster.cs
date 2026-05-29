using PetBox.Log.Core.Data;

namespace PetBox.Log.Core.Ingestion;

public interface ITailBroadcaster
{
	IAsyncEnumerable<LogEntryRecord> Subscribe(string projectKey, string logName, CancellationToken ct);
	void Publish(string projectKey, string logName, IReadOnlyList<LogEntryRecord> batch);
}
