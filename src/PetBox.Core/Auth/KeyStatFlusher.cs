using LinqToDB.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;

namespace PetBox.Core.Auth;

// spec apikey-last-used — the persisting half. Drains IKeyStatService every ~5 minutes and folds the
// whole batch into ApiKeys.LastUsedAt with ONE statement (100 used keys = 1 UPDATE, not 100), on a
// connection taken from ICoreDbFactory — PetBoxDb is not injectable (AGENTS.md invariant), and a
// singleton holding a DataConnection is exactly the bug that rule exists to make unexpressible.
//
// Same skeleton as MemoryUsageRecorder's drain loop / WalCheckpointService's tick: a failure is
// logged and the loop lives on — telemetry never takes the process down.
//
// HONEST LIMIT: a hard kill loses up to one interval's marks. StopAsync flushes, so an orderly
// shutdown (SIGTERM, `docker stop`, a deploy) keeps them.
public sealed partial class KeyStatFlusher(
	IKeyStatService stats,
	ICoreDbFactory coreDb,
	ILogger<KeyStatFlusher> logger) : BackgroundService
{
	public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

	// SQLite takes far more than this, but a bounded chunk keeps one statement's parameter list
	// (2 per key) comfortably inside every provider limit — and a fleet with >500 keys used inside
	// one window is still 1-2 statements, not 500.
	const int ChunkSize = 500;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try { await Task.Delay(Interval, stoppingToken); }
			catch (OperationCanceledException) { break; }

			try
			{
				await FlushAsync(stoppingToken);
			}
			catch (OperationCanceledException) { break; }
			catch (Exception ex)
			{
				// A lost flush costs freshness, nothing else — the marks stay in memory, and a key
				// that is still being used will be re-stamped anyway.
				LogFlushFailed(logger, ex);
			}
		}
	}

	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		// The cheap half of the restart-loss problem: on an orderly shutdown, nothing is lost.
		try { await FlushAsync(CancellationToken.None); }
		catch (Exception ex) { LogFlushFailed(logger, ex); }
		await base.StopAsync(cancellationToken);
	}

	// Returns the number of keys written — the tests' handle on "the batch actually landed".
	public async Task<int> FlushAsync(CancellationToken ct = default)
	{
		var batch = stats.DrainDirty();
		if (batch.Count == 0) return 0;

		using var db = coreDb.Open();
		var written = 0;
		for (var offset = 0; offset < batch.Count; offset += ChunkSize)
		{
			var chunk = batch.Skip(offset).Take(ChunkSize).ToList();

			// ONE statement per chunk. The CTE carries (key, timestamp) pairs; the UPDATE joins the
			// table against it. Parameterized — a key value never reaches the SQL text.
			var values = string.Join(",", chunk.Select((_, i) => $"(@k{i},@t{i})"));
			var parameters = new DataParameter[chunk.Count * 2];
			for (var i = 0; i < chunk.Count; i++)
			{
				parameters[i * 2] = new DataParameter($"k{i}", chunk[i].Key);
				parameters[i * 2 + 1] = new DataParameter($"t{i}", chunk[i].Value, LinqToDB.DataType.DateTime);
			}

			written += await db.ExecuteAsync($"""
				WITH v(k, t) AS (VALUES {values})
				UPDATE ApiKeys
				SET LastUsedAt = (SELECT t FROM v WHERE v.k = ApiKeys."Key")
				WHERE ApiKeys."Key" IN (SELECT k FROM v)
				""", ct, parameters);
		}
		return written;
	}

	[LoggerMessage(EventId = 320, Level = LogLevel.Warning, Message = "ApiKey last-used flush failed")]
	static partial void LogFlushFailed(ILogger logger, Exception ex);
}
