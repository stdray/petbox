using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PetBox.Log.Core.Data;

namespace PetBox.Log.Core.Ingestion;

public sealed class InMemoryTailBroadcaster : ITailBroadcaster
{
	readonly ConcurrentDictionary<string, ImmutableList<ChannelWriter<LogEntryRecord>>> _subscribers = new();

	public int WindowSize { get; init; } = 200;

	public async IAsyncEnumerable<LogEntryRecord> Subscribe(
		string projectKey,
		string logName,
		[EnumeratorCancellation] CancellationToken ct)
	{
		var key = $"{projectKey}/{logName}";
		var channel = Channel.CreateBounded<LogEntryRecord>(new BoundedChannelOptions(WindowSize)
		{
			FullMode = BoundedChannelFullMode.DropOldest,
			SingleReader = true,
			SingleWriter = false,
		});

		_subscribers.AddOrUpdate(
			key,
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
				key,
				_ => [],
				(_, list) => list.Remove(channel.Writer));
			channel.Writer.TryComplete();
		}
	}

	public void Publish(string projectKey, string logName, IReadOnlyList<LogEntryRecord> batch)
	{
		if (batch.Count == 0) return;
		if (!_subscribers.TryGetValue($"{projectKey}/{logName}", out var writers) || writers.Count == 0)
			return;

		foreach (var writer in writers)
		{
			foreach (var record in batch)
				writer.TryWrite(record);
		}
	}
}
