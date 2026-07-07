using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Kusto.Language;
using LinqToDB;
using LinqToDB.Data;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Query;

// KqlBench — an ISOLATED-PROCESS benchmark runner for one (backend x size) combo. Spawned by
// tools/KqlBench/run-bench.ps1 once per combo so DuckDB's NATIVE memory (outside the .NET GC heap) is
// measured as this process's PeakWorkingSet64 without conflating the two backends. It ingests a synthetic
// log into a FILE-backed DB, runs the fixed KQL query set, and prints ONE JSON line of metrics, then exits.
//
// Usage:  KqlBench <sqlite|duckdb> <rowCount> [warmup] [measured]
//   e.g.  KqlBench sqlite 100000
//         KqlBench duckdb 1000000 2 5
internal static class Program
{
	// How many distinct ServiceKeys the dataset spreads across. Keeps the self-join fan-out and
	// distinct/summarize cardinalities realistic (not 1 giant group, not all-unique).
	const int ServiceCount = 50;

	// The self-join and the mv-expand are bounded to a fixed row prefix so their cost is comparable
	// across sizes and never blows up at 1M (a 1M x 1M self-join would be catastrophic).
	const int JoinPrefixRows = 2000;
	const int MvExpandPrefixRows = 50000;

	// Ingest in batches so a 1M/10M run never holds the whole dataset in a List (which would itself
	// inflate the working-set we are trying to attribute to the STORE).
	const int IngestBatch = 100_000;

	static readonly BulkCopyOptions KeepIds = new() { KeepIdentity = true };

	// The fixed query set — IMPLEMENTED operators only (no percentile/make_list/make_set/dcount).
	// {P} placeholders are substituted with the bounded prefixes above.
	static readonly (string Name, string Kql)[] Queries =
	[
		("summarize_bin_1h", "events | summarize count(), sum(Id) by bin(Timestamp, 1h)"),
		("top_100_by_id", "events | top 100 by Id desc"),
		("self_join_servicekey",
			$"events | where Id <= {JoinPrefixRows} | join kind=inner (events | where Id <= {JoinPrefixRows}) on ServiceKey | project Id, Id1"),
		("distinct_service_level", "events | distinct ServiceKey, Level"),
		("where_project", "events | where Level >= 3 | project Id, ServiceKey, Level, Message"),
		("mv_expand_tags",
			$"events | where Id <= {MvExpandPrefixRows} | mv-expand Properties.tags | project Id, tags"),
	];

	static async Task<int> Main(string[] args)
	{
		if (args.Length < 2)
		{
			Console.Error.WriteLine("usage: KqlBench <sqlite|duckdb> <rowCount> [warmup] [measured]");
			return 2;
		}

		var backend = args[0].Trim().ToLowerInvariant();
		if (backend is not ("sqlite" or "duckdb"))
		{
			Console.Error.WriteLine($"unknown backend '{backend}' (want sqlite|duckdb)");
			return 2;
		}

		if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) || size <= 0)
		{
			Console.Error.WriteLine($"bad rowCount '{args[1]}'");
			return 2;
		}

		var warmup = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 2;
		var measured = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 5;

		var tempDir = Path.Combine(Path.GetTempPath(), $"kqlbench-{backend}-{size}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);
		var proc = Process.GetCurrentProcess();

		try
		{
			var isDuck = backend == "duckdb";

			// ---- axis 5: cold-start / idle footprint — open a FRESH connection on an empty store and run a
			// trivial `events | take 1`. First DuckDB connection loads the native lib here; that one-time
			// cost is exactly what we want to attribute to cold-start.
			var coldPath = Path.Combine(tempDir, "cold" + (isDuck ? ".duckdb" : ".db"));
			var coldSw = Stopwatch.StartNew();
			using (var coldDb = OpenStore(coldPath, isDuck))
			{
				var coldCode = KustoCode.Parse("events | take 1");
				await DrainAsync(KqlTransformer.Execute(coldDb.LogEntries, coldCode, options: OptFor(isDuck)));
			}
			coldSw.Stop();
			proc.Refresh();
			var coldStartMs = coldSw.Elapsed.TotalMilliseconds;
			var coldStartWorkingSetBytes = proc.WorkingSet64;

			// ---- open the main store + ingest ----
			var dbPath = Path.Combine(tempDir, "log" + (isDuck ? ".duckdb" : ".db"));
			using var db = OpenStore(dbPath, isDuck);

			proc.Refresh();
			var wsBeforeIngest = proc.WorkingSet64;

			// ---- axis 3: ingest rows/sec + working-set delta ----
			var ingestSw = Stopwatch.StartNew();
			IngestBatched(db, size);
			ingestSw.Stop();
			proc.Refresh();
			var wsAfterIngest = proc.WorkingSet64;
			var ingestSeconds = ingestSw.Elapsed.TotalSeconds;
			var ingestRowsPerSec = ingestSeconds > 0 ? size / ingestSeconds : 0;

			// ---- axis 2 + a per-query row-count sanity: warmup then N measured, p50/p95 ----
			var queryResults = new List<object>(Queries.Length);
			foreach (var (name, kql) in Queries)
			{
				QueryMetric metric;
				try
				{
					metric = await BenchmarkQueryAsync(db, kql, isDuck, warmup, measured);
				}
				catch (Exception ex)
				{
					// Per the brief: if a query errors on a backend, drop it and note.
					metric = new QueryMetric(name, kql, 0, -1, -1, -1, ex.GetType().Name + ": " + ex.Message);
					queryResults.Add(metric with { Name = name });
					continue;
				}
				queryResults.Add(metric with { Name = name });
			}

			// ---- axis 4: on-disk size — dispose the connection so the store flushes, then sum the db files.
			db.Dispose();
			var onDiskFileBytes = Directory.EnumerateFiles(tempDir)
				.Where(f => Path.GetFileName(f).StartsWith("log", StringComparison.Ordinal))
				.Sum(f => new FileInfo(f).Length);

			// ---- axis 1: peak working set for the whole workload, read at exit (captures DuckDB native mem).
			proc.Refresh();
			var peakWorkingSetBytes = proc.PeakWorkingSet64;

			var report = new
			{
				backend,
				size,
				warmup,
				measured,
				peakWorkingSetBytes,
				coldStartMs,
				coldStartWorkingSetBytes,
				ingestSeconds,
				ingestRowsPerSec,
				ingestWorkingSetBeforeBytes = wsBeforeIngest,
				ingestWorkingSetAfterBytes = wsAfterIngest,
				ingestWorkingSetDeltaBytes = wsAfterIngest - wsBeforeIngest,
				onDiskFileBytes,
				queries = queryResults,
			};

			Console.WriteLine(JsonSerializer.Serialize(report));
			return 0;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"FATAL {backend}/{size}: {ex}");
			return 1;
		}
		finally
		{
			try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort temp cleanup */ }
		}
	}

	static LogDb OpenStore(string path, bool isDuck)
	{
		if (isDuck)
		{
			var db = new LogDb(LogDb.CreateDuckDbOptions($"DataSource={path}"));
			LogSchemaDuckDb.Ensure(db);
			return db;
		}

		var cs = $"Data Source={path}";
		LogSchema.Ensure(cs);
		return new LogDb(LogDb.CreateOptions(cs));
	}

	static KqlTranslationOptions OptFor(bool isDuck) =>
		new() { Dialect = isDuck ? KqlDialect.DuckDb : KqlDialect.Sqlite };

	static void IngestBatched(LogDb db, int size)
	{
		var baseMs = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
		var buffer = new List<LogEntryRecord>(Math.Min(IngestBatch, size));
		for (var i = 0; i < size; i++)
		{
			buffer.Add(new LogEntryRecord
			{
				Id = i + 1,
				ServiceKey = "svc-" + (i % ServiceCount).ToString(CultureInfo.InvariantCulture),
				// 1s apart → a spread of realistic bin(1h) buckets (≈ size/3600 buckets).
				TimestampMs = baseMs + (long)i * 1000L,
				Level = i % 6,
				Message = "event message " + (i % 1000).ToString(CultureInfo.InvariantCulture),
				MessageTemplate = "event message {n}",
				Exception = null,
				// A small tags array so mv-expand fans out ~2x; a couple of scalar bag keys alongside.
				PropertiesJson = """{"tags":["a","b"],"code":"200","region":"eu"}""",
				TemplateHash = 0,
			});
			if (buffer.Count >= IngestBatch)
			{
				db.LogEntries.BulkCopy(KeepIds, buffer);
				buffer.Clear();
			}
		}
		if (buffer.Count > 0)
			db.LogEntries.BulkCopy(KeepIds, buffer);
	}

	static async Task<QueryMetric> BenchmarkQueryAsync(LogDb db, string kql, bool isDuck, int warmup, int measured)
	{
		var opts = OptFor(isDuck);
		var rowCount = 0L;

		for (var w = 0; w < warmup; w++)
			rowCount = await RunOnceAsync(db, kql, opts);

		var samplesMs = new double[measured];
		for (var m = 0; m < measured; m++)
		{
			var sw = Stopwatch.StartNew();
			rowCount = await RunOnceAsync(db, kql, opts);
			sw.Stop();
			samplesMs[m] = sw.Elapsed.TotalMilliseconds;
		}

		Array.Sort(samplesMs);
		return new QueryMetric(
			Name: "",
			Kql: kql,
			RowCount: rowCount,
			P50Ms: Percentile(samplesMs, 0.50),
			P95Ms: Percentile(samplesMs, 0.95),
			MinMs: samplesMs[0],
			Error: null);
	}

	static async Task<long> RunOnceAsync(LogDb db, string kql, KqlTranslationOptions opts)
	{
		var code = KustoCode.Parse(kql);
		return await DrainAsync(KqlTransformer.Execute(db.LogEntries, code, options: opts));
	}

	static async Task<long> DrainAsync(KqlResult result)
	{
		var n = 0L;
		await foreach (var _ in result.Rows)
			n++;
		return n;
	}

	// Linear-interpolation percentile over a pre-sorted ascending sample.
	static double Percentile(double[] sorted, double p)
	{
		if (sorted.Length == 0) return double.NaN;
		if (sorted.Length == 1) return sorted[0];
		var rank = p * (sorted.Length - 1);
		var lo = (int)Math.Floor(rank);
		var hi = (int)Math.Ceiling(rank);
		if (lo == hi) return sorted[lo];
		var frac = rank - lo;
		return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
	}

	sealed record QueryMetric(
		string Name, string Kql, long RowCount, double P50Ms, double P95Ms, double MinMs, string? Error);
}
