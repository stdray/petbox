namespace YobaBox.Log.Core.Ingestion;

public sealed record IngestionOptions
{
	public int ChannelCapacity { get; init; } = 10_000;
	public int MaxBatchSize { get; init; } = 1_000;
}
