using PetBox.Core.Data;
using PetBox.Memory.Contract;

namespace PetBox.Web.Search;

// Quarantine self-cleaning (spec: memoverhaul — "записи, не подтвердившие ценность,
// выводятся из оборота восстановимо"). The autocapture store fills with machine-distilled
// facts; those that age past MinAge and cannot show they earned their keep are retired.
//
// The rule is TWO-DIMENSIONAL (spec: usage-cost-and-fit-separate). The old one — "old and
// never deliberately searched, never opened" — was an IMPRESSION test, and it retired exactly
// the wrong entries: a fact so good that its snippet answers the question every time is
// surfaced constantly and opened NEVER, so an opened-count of 0 marked the store's best index
// row for death. Cost and fit are what actually separate the two outcomes, and delivery_events
// records them:
//   COST = DeliveredChars — the body chars this entry poured into callers' context in the window.
//   FIT  = AvgKRel — the mean within-request fit (0..1 against each request's best hit) of those
//          deliveries.
// A NOISE BOAR is expensive AND off-target (chars >= MinDeliveredChars, fit < MaxAvgKRel) → a
// candidate. A PRECISE INDEX row is cheap and dead-on → kept, no matter how few times it was
// opened. An entry with no cost and no reach at all (never delivered, never deliberately
// searched, never opened) is still dead weight → a candidate, as before.
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
//
// COST/FIT AXIS (card usage-delivery-mixes-machine-traffic, spec memory-usage-observability
// "Учёт НЕ ДОЛЖЕН включать внутренний машинный трафик"): DeliveredChars/AvgKRel above are
// computed BOTH ways by MemoryService's DeliveryRollup — combined (every UsageSource) and
// deliberate-only — and this job picks which one it judges on via ExcludeMachineFromCost
// (config `Memory:QuarantineGc:ExcludeMachineFromCost`, default false = unchanged combined
// behaviour). Config, not a hardcoded switch: a hard deliberate-only filter would make an
// entry's MACHINE-driven cost invisible and a store served mostly by automation would start
// reading as "never reached" — the config default keeps today's behaviour until an operator has
// reviewed a report-only before/after candidate diff on live data and opts in deliberately.

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

	private static readonly TimeSpan DefaultMinAge = TimeSpan.FromDays(30);
	private static readonly TimeSpan DefaultScanInterval = TimeSpan.FromHours(6);

	// The window the cost/fit verdict is measured over: RECENT behaviour, not the entry's whole
	// life — an entry that was useful a year ago and is pure ballast today must be retirable.
	// 30d matches MinAge (an entry is judged over roughly the span that made it eligible).
	private static readonly TimeSpan DefaultUsageWindow = TimeSpan.FromDays(30);

	// "Expensive": body chars this entry poured into callers' context within the window. 10k
	// chars ≈ 2.5k tokens of somebody's context spent on ONE entry — for a machine-distilled
	// quarantine fact that is a real bill, and it takes several deliveries to run up, so a
	// one-off unlucky hit can never trip it.
	private const long DefaultMinDeliveredChars = 10_000;

	// "Off-target": the mean kRel of those deliveries. kRel is the row's fused score over the
	// request's TOP-1 score, so 1.0 = it was the best answer, and a value below 0.5 means the
	// row was, on average, less than half as good as whatever actually answered the query — it
	// rode along as filler. Deliberately conservative: the cost leg (10k chars) must ALSO fire,
	// so retiring takes a sustained pattern of expensive filler, not one bad query.
	private const double DefaultMaxAvgKRel = 0.5;

	readonly IProjectCatalog _catalog;
	readonly IMemoryService _memory;
	readonly ILogger<MemoryQuarantineGcJob>? _logger;
	readonly TimeSpan _minAge;
	readonly bool _enforce;
	readonly TimeSpan _scanInterval;
	readonly TimeSpan _usageWindow;
	readonly long _minDeliveredChars;
	readonly double _maxAvgKRel;
	readonly MemoryQuarantineGcClock _clock;
	// The cost/fit AXIS this verdict judges on (card usage-delivery-mixes-machine-traffic, spec
	// memory-usage-observability "Учёт НЕ ДОЛЖЕН включать внутренний машинный трафик"). false
	// (default, config Memory:QuarantineGc:ExcludeMachineFromCost) = the pre-existing behaviour,
	// unchanged: cost/fit are the COMBINED totals over every UsageSource. true = cost/fit are the
	// DELIBERATE-ONLY split — a store/entry kept alive mostly by automated (machine) pulls no
	// longer has that traffic count toward "reached" or toward "fits". Config-driven, not
	// hardcoded: the owner reviews a report-only before/after candidate diff on live data before
	// ever flipping this (see the card's Risk section — narrowing blind risks marking an
	// automation-served store "never reached").
	readonly bool _excludeMachineFromCost;

	public MemoryQuarantineGcJob(IProjectCatalog catalog, IMemoryService memory,
		ILogger<MemoryQuarantineGcJob>? logger = null, TimeSpan? minAge = null, bool enforce = false,
		TimeSpan? scanInterval = null, MemoryQuarantineGcClock? clock = null,
		TimeSpan? usageWindow = null, long? minDeliveredChars = null, double? maxAvgKRel = null,
		bool excludeMachineFromCost = false)
	{
		_catalog = catalog;
		_memory = memory;
		_logger = logger;
		_minAge = minAge ?? DefaultMinAge;
		_enforce = enforce;
		_scanInterval = scanInterval ?? DefaultScanInterval;
		_clock = clock ?? new MemoryQuarantineGcClock();
		_usageWindow = usageWindow ?? DefaultUsageWindow;
		_minDeliveredChars = minDeliveredChars ?? DefaultMinDeliveredChars;
		_maxAvgKRel = maxAvgKRel ?? DefaultMaxAvgKRel;
		_excludeMachineFromCost = excludeMachineFromCost;
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
				// Cost/fit over the WINDOW (GetUsageAsync's own cost/fit is all-time — right for a
				// read surface, wrong for a verdict: it can never forget).
				var cost = await _memory.GetDeliveryStatsAsync(project, Store, _usageWindow, keys: null, ct);

				var candidates = entries
					.Where(e => e.Created <= cutoff)
					.Where(e => IsCandidate(usage.GetValueOrDefault(e.Key), cost.GetValueOrDefault(e.Key)))
					.Select(e => e.Key)
					.ToList();
				if (candidates.Count == 0) continue;

				if (_enforce)
				{
					// Soft-delete (history kept) via the service — recoverable retirement.
					await _memory.UpsertAsync(project, Store,
						Array.Empty<MemoryEntryInput>(),
						candidates.Select(k => new MemoryDelete(k, 0)).ToList(), ct: ct);
					retired += candidates.Count;
					if (_logger?.IsEnabled(LogLevel.Information) == true)
						_logger.LogInformation(
							"memory quarantine GC ENFORCED: retired {Count} unproven autocaptured entries older than {MinAgeDays}d in {Project}/{Store} (never reached, or >={MinDeliveredChars} delivered chars at mean fit <{MaxAvgKRel} over {WindowDays}d): {Keys}",
							candidates.Count, _minAge.TotalDays, project, Store, _minDeliveredChars, _maxAvgKRel,
							_usageWindow.TotalDays, string.Join(", ", candidates));
				}
				else if (_logger?.IsEnabled(LogLevel.Information) == true)
				{
					_logger.LogInformation(
						"memory quarantine GC (report-only): {Count} unproven autocaptured entries older than {MinAgeDays}d in {Project}/{Store} would be retired (never reached, or >={MinDeliveredChars} delivered chars at mean fit <{MaxAvgKRel} over {WindowDays}d; set enforce to act): {Keys}",
						candidates.Count, _minAge.TotalDays, project, Store, _minDeliveredChars, _maxAvgKRel,
						_usageWindow.TotalDays, string.Join(", ", candidates));
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

	// The two-dimensional verdict on ONE aged entry (cost/fit are the window's; the counters are
	// all-time). Two disjoint ways to be dead weight — and one loud way to be kept:
	//
	//   never reached — no delivery in the window AND no deliberate search AND never opened: it
	//     has cost nothing because it has done nothing. The old rule, minus its false positive.
	//   noise boar    — delivered >= MinDeliveredChars of body while its mean fit stayed below
	//     MaxAvgKRel: it keeps being paid for and keeps not being the answer.
	//
	// Everything else is spared, and the case that matters is the one the old rule killed: an
	// entry delivered a lot with a HIGH fit (the snippet that answers the question, so nobody
	// ever opens it) has cost > 0 → not "never reached", and fit >= the floor → not a boar.
	// An entry with cost but no fit at all (only ever delivered by a listing, KRel null) is also
	// spared: a listing is curation, and we do not retire on evidence we never gathered.
	bool IsCandidate(MemoryUsageView? u, MemoryDeliveryStats? d)
	{
		// The AXIS: combined (every UsageSource, unchanged default) or deliberate-only, per
		// _excludeMachineFromCost. Picking the pair up front keeps the two verdict legs below
		// blind to which axis is live — same formulas either way.
		var deliveredChars = _excludeMachineFromCost ? d?.DeliberateDeliveredChars ?? 0 : d?.DeliveredChars ?? 0;
		var avgKRel = _excludeMachineFromCost ? d?.DeliberateAvgKRel : d?.AvgKRel;
		var neverReached = deliveredChars == 0 && (u is null || (u.Deliberate == 0 && u.Opened == 0));
		var noiseBoar = deliveredChars >= _minDeliveredChars && avgKRel is { } fit && fit < _maxAvgKRel;
		return neverReached || noiseBoar;
	}
}
