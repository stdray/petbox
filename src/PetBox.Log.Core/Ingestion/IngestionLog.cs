using Microsoft.Extensions.Logging;

namespace PetBox.Log.Core.Ingestion;

static partial class IngestionLog
{
	[LoggerMessage(EventId = 1, Level = LogLevel.Error,
		Message = "Failed to append batch of {Count} events to project {Project}")]
	public static partial void AppendBatchFailed(ILogger logger, Exception ex, int count, string project);

	[LoggerMessage(EventId = 2, Level = LogLevel.Warning,
		Message = "Ingestion shutdown timed out; some batches may not have been flushed")]
	public static partial void ShutdownTimedOut(ILogger logger);
}
