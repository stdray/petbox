using Microsoft.Extensions.Hosting;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Ingestion;
using PetBox.Log.Core.Models;

namespace PetBox.Log.Core.SelfLogging;

public sealed class SystemLogFlusher : BackgroundService
{
	readonly IIngestionPipeline _pipeline;
	readonly SystemLoggerProvider _provider;

	public SystemLogFlusher(IIngestionPipeline pipeline, SystemLoggerProvider provider)
	{
		_pipeline = pipeline;
		_provider = provider;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var batchSize = _provider.Options.BatchSize;
		var batch = new List<LogEntryCandidate>(batchSize);

		while (!stoppingToken.IsCancellationRequested)
		{
			batch.Clear();
			try
			{
				if (!await _provider.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
					return;

				while (batch.Count < batchSize && _provider.Reader.TryRead(out var ev))
					batch.Add(ev);
			}
			catch (OperationCanceledException)
			{
				break;
			}

			if (batch.Count == 0)
				continue;

			try
			{
				await _pipeline.IngestAsync(LogNames.SystemProject, LogNames.SelfLog, batch, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				// Bypass ILogger — recursion would be ours. Console.Error is the safe sink.
				await Console.Error.WriteLineAsync($"PetBox self-log flush failed: {ex.Message}").ConfigureAwait(false);
			}
		}
	}
}
