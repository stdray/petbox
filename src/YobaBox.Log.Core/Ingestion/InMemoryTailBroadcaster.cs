using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using YobaBox.Log.Core.Data;

namespace YobaBox.Log.Core.Ingestion;

public sealed class InMemoryTailBroadcaster : ITailBroadcaster
{
	readonly ConcurrentDictionary<string, ImmutableList<ChannelWriter<LogEntryRecord>>> _subscribers = new();

	public int WindowSize { get; init; } = 200;

	public async IAsyncEnumerable<LogEntryRecord> Subscribe(
		string projectKey,
		[EnumeratorCancellation] CancellationToken ct)
	{
		var channel = Channel.CreateBounded<LogEntryRecord>(new BoundedChannelOptions(WindowSize)
		{
			FullMode = BoundedChannelFullMode.DropOldest,
			SingleReader = true,
			SingleWriter = false,
		});

		_subscribers.AddOrUpdate(
			projectKey,
			_ => [channel.Writer],
			(_, list) => list.Add(channel.Writer));

		try
		{
			await foreach (var record in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
				yield return record;
		}
		finally
		{
			_subscribers.AddOrUpdate(
				projectKey,
				_ => [],
				(_, list) => list.Remove(channel.Writer));
			channel.Writer.TryComplete();
		}
	}

	public void Publish(string projectKey, IReadOnlyList<LogEntryRecord> batch)
	{
		if (batch.Count == 0) return;
		if (!_subscribers.TryGetValue(projectKey, out var writers) || writers.Count == 0)
			return;

		foreach (var writer in writers)
		{
			foreach (var record in batch)
				writer.TryWrite(record);
		}
	}
}
