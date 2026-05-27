namespace YobaBox.Core.Settings;

// Channel-pipeline tuning for log ingestion. System-level only (one
// ChannelIngestionPipeline singleton per process).
public sealed record IngestionSettings
{
	[Setting(TopLevel = Scope.System, Key = "log.ingestion.channelCapacity",
		Description = "Per-project bounded-channel capacity. Writers block when full.")]
	public int ChannelCapacity { get; init; } = 10_000;

	[Setting(TopLevel = Scope.System, Key = "log.ingestion.maxBatchSize",
		Description = "Max events flushed in one BulkCopyAsync.")]
	public int MaxBatchSize { get; init; } = 1_000;
}
