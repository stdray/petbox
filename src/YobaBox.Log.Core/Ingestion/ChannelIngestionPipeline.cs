using System.Collections.Concurrent;
using System.Threading.Channels;
using LinqToDB.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YobaBox.Core.Settings;
using YobaBox.Log.Core.Data;
using YobaBox.Log.Core.Models;
using YobaBox.Log.Core.Observability;

namespace YobaBox.Log.Core.Ingestion;

public sealed class ChannelIngestionPipeline : IIngestionPipeline, IHostedService, IAsyncDisposable
{
	readonly ILogDbFactory _factory;
	readonly ITailBroadcaster? _tail;
	readonly IServiceScopeFactory _scopeFactory;
	readonly ILogger<ChannelIngestionPipeline> _logger;
	readonly ConcurrentDictionary<string, ProjectChannel> _channels = new(StringComparer.Ordinal);
	IngestionSettings _settings = new();
	int _disposed;

	public ChannelIngestionPipeline(
		ILogDbFactory factory,
		IServiceScopeFactory scopeFactory,
		ILogger<ChannelIngestionPipeline> logger,
		ITailBroadcaster? tail = null)
	{
		_factory = factory;
		_tail = tail;
		_scopeFactory = scopeFactory;
		_logger = logger;
	}

	public async ValueTask IngestAsync(string projectKey, IReadOnlyList<LogEntryCandidate> batch, CancellationToken ct)
	{
		if (batch.Count == 0)
			return;

		using var activity = projectKey == "$system"
			? null
			: ActivitySources.Ingestion.StartActivity("ingest.enqueue");
		activity?.SetTag("project", projectKey);
		activity?.SetTag("batch.size", batch.Count);

		var wc = _channels.GetOrAdd(projectKey, StartChannel);
		foreach (var candidate in batch)
			await wc.Writer.WriteAsync(candidate, ct).ConfigureAwait(false);
	}

	ProjectChannel StartChannel(string projectKey)
	{
		var channel = Channel.CreateBounded<LogEntryCandidate>(new BoundedChannelOptions(_settings.ChannelCapacity)
		{
			FullMode = BoundedChannelFullMode.Wait,
			SingleReader = true,
			SingleWriter = false,
		});
		var loop = Task.Run(() => WriterLoopAsync(projectKey, channel.Reader));
		return new ProjectChannel(channel.Writer, loop);
	}

	async Task WriterLoopAsync(string projectKey, ChannelReader<LogEntryCandidate> reader)
	{
		var batch = new List<LogEntryCandidate>(_settings.MaxBatchSize);
		while (await reader.WaitToReadAsync().ConfigureAwait(false))
		{
			batch.Clear();
			while (batch.Count < _settings.MaxBatchSize && reader.TryRead(out var candidate))
				batch.Add(candidate);

			if (batch.Count == 0)
				continue;

			try
			{
				var records = new List<LogEntryRecord>(batch.Count);
				foreach (var c in batch)
					records.Add(LogEntryRecord.FromCandidate(c, LogEntryRecord.ComputeTemplateHash(c.MessageTemplate)));

				var logDb = _factory.GetLogDb(projectKey);
				await logDb.LogEntries.BulkCopyAsync(records, CancellationToken.None).ConfigureAwait(false);
				_tail?.Publish(projectKey, records);
			}
			catch (Exception ex)
			{
				IngestionLog.AppendBatchFailed(_logger, ex, batch.Count, projectKey);
			}
		}
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		// Snapshot settings once. Pipeline is a singleton — channel sizes can't
		// hot-reload without recreating channels (their capacity is baked at create).
		using var scope = _scopeFactory.CreateScope();
		var resolver = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
		_settings = await resolver.GetAsync<IngestionSettings>(Scope.System, "$", cancellationToken).ConfigureAwait(false);
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		foreach (var wc in _channels.Values)
			wc.Writer.TryComplete();

		try
		{
			await Task.WhenAll(_channels.Values.Select(w => w.Loop))
				.WaitAsync(cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			IngestionLog.ShutdownTimedOut(_logger);
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		foreach (var wc in _channels.Values)
			wc.Writer.TryComplete();

		await Task.WhenAll(_channels.Values.Select(w => w.Loop)).ConfigureAwait(false);
	}

	sealed record ProjectChannel(ChannelWriter<LogEntryCandidate> Writer, Task Loop);
}
