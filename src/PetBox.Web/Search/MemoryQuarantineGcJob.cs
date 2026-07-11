using PetBox.Core.Data;
using PetBox.Memory.Contract;

namespace PetBox.Web.Search;

// Quarantine self-cleaning (spec: memoverhaul — "записи, не подтвердившие ценность,
// выводятся из оборота восстановимо"). The autocapture store fills with machine-distilled
// facts; those that age past MinAge without ONE deliberate reach (a human/agent search or a
// direct open — automatic hook pulls do NOT count, hence the DeliberateCount signal) have
// not proven their worth and are retired.
//
// Two gears:
//   report-only (Enforce=false, the default) — a structured log of the candidate keys, so an
//     operator can watch what enforce WOULD retire before turning it on. Nothing is written.
//   enforce (Enforce=true) — each candidate is soft-deleted through IMemoryService: the entry
//     is temporally closed, its history kept, and it can be recovered.
//
// Scope invariants: ONLY the `autocaptured` quarantine store is ever touched — never notes,
// never ops, never a system store. A machine write's home is the only place a machine may prune.
// Runs on the shared enrichment tick like the other jobs, but throttled to ScanInterval so it
// doesn't re-log the same stable candidate set every 60s.

// Shared last-scan clock for the throttle. The job is scoped (a fresh instance per enrichment
// tick), so the "when did we last scan" state must live in a singleton injected across ticks
// (registered in Program.cs). Kept out of a process-static so tests are deterministic.
public sealed class MemoryQuarantineGcClock
{
	public DateTime LastScan { get; set; } = DateTime.MinValue;
}

public sealed class MemoryQuarantineGcJob : IBackgroundIndexJob
{
	// The one store this GC may touch — the machine-write quarantine (kept in sync with
	// SessionFactsJob.Store). notes/ops/system stores are curated and never swept here.
	public const string Store = SessionFactsJob.Store;

	public static readonly TimeSpan DefaultMinAge = TimeSpan.FromDays(30);
	public static readonly TimeSpan DefaultScanInterval = TimeSpan.FromHours(6);

	readonly IProjectCatalog _catalog;
	readonly IMemoryService _memory;
	readonly ILogger<MemoryQuarantineGcJob>? _logger;
	readonly TimeSpan _minAge;
	readonly bool _enforce;
	readonly TimeSpan _scanInterval;
	readonly MemoryQuarantineGcClock _clock;

	public MemoryQuarantineGcJob(IProjectCatalog catalog, IMemoryService memory,
		ILogger<MemoryQuarantineGcJob>? logger = null, TimeSpan? minAge = null, bool enforce = false,
		TimeSpan? scanInterval = null, MemoryQuarantineGcClock? clock = null)
	{
		_catalog = catalog;
		_memory = memory;
		_logger = logger;
		_minAge = minAge ?? DefaultMinAge;
		_enforce = enforce;
		_scanInterval = scanInterval ?? DefaultScanInterval;
		_clock = clock ?? new MemoryQuarantineGcClock();
	}

	public async Task<int> DrainAllAsync(CancellationToken ct)
	{
		var now = DateTime.UtcNow;
		if (now - _clock.LastScan < _scanInterval) return 0; // throttle — the candidate set is stable
		_clock.LastScan = now;

		var cutoff = now - _minAge;
		var retired = 0;
		// Catalog, not file scan (spec: catalog-is-source-of-truth): the sweep list is the projects
		// that HAVE memory per core.db `MemoryStores`, not the memory/*.db files on disk. A file scan
		// would sweep the GHOST file of a deleted project (and, under enforce, write soft-deletes back
		// into it — resurrecting a file MemoryOrphanCleanupService is trying to reclaim), and would
		// miss a project whose store row exists but whose file has not been materialized yet. The
		// StoreExistsAsync gate below is itself a catalog read, so no file is opened for a project
		// that has no quarantine store.
		foreach (var project in await _catalog.ListMemoryProjectKeysAsync(ct))
		{
			ct.ThrowIfCancellationRequested();
			try
			{
				if (!await _memory.StoreExistsAsync(project, Store, ct)) continue;

				// GetDb (via the service reads below) runs the migrations so entry_usage carries
				// DeliberateCount even for a file last opened before M008.
				var entries = await _memory.ListActiveEntriesAsync(project, Store, ct);
				if (entries.Count == 0) continue;
				var usage = await _memory.GetUsageAsync(project, Store, keys: null, ct);

				// A candidate is old enough AND has never proven value: no deliberate search
				// surfaced it and it was never opened. Automatic machine pulls (SurfacedCount
				// without DeliberateCount) are explicitly ignored — that is the whole point.
				var candidates = entries
					.Where(e => e.Created <= cutoff)
					.Where(e => !usage.TryGetValue(e.Key, out var u) || (u.Deliberate == 0 && u.Opened == 0))
					.Select(e => e.Key)
					.ToList();
				if (candidates.Count == 0) continue;

				if (_enforce)
				{
					// Soft-delete (history kept) via the service — recoverable retirement.
					await _memory.UpsertAsync(project, Store,
						Array.Empty<MemoryEntryInput>(),
						candidates.Select(k => new MemoryDelete(k, 0)).ToList(), ct);
					retired += candidates.Count;
					if (_logger?.IsEnabled(LogLevel.Information) == true)
						_logger.LogInformation(
							"memory quarantine GC ENFORCED: retired {Count} unproven autocaptured entries older than {MinAgeDays}d in {Project}/{Store}: {Keys}",
							candidates.Count, _minAge.TotalDays, project, Store, string.Join(", ", candidates));
				}
				else if (_logger?.IsEnabled(LogLevel.Information) == true)
				{
					_logger.LogInformation(
						"memory quarantine GC (report-only): {Count} unproven autocaptured entries older than {MinAgeDays}d in {Project}/{Store} would be retired (set enforce to act): {Keys}",
						candidates.Count, _minAge.TotalDays, project, Store, string.Join(", ", candidates));
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// One broken store must not stall the sweep of the rest; retries next scan.
				_logger?.LogError(ex, "memory quarantine GC failed for {Project}/{Store}; skipped", project, Store);
			}
		}
		return retired;
	}
}
