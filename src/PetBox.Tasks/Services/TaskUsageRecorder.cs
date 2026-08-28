using System.Collections.Concurrent;
using System.Threading.Channels;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;

namespace PetBox.Tasks.Services;

// The one writer of node_usage and node_delivery_events. Singleton: increments and delivery
// events are enqueued onto a bounded channel and drained by a background loop, so the read path
// that reports a hit never waits on SQLite. Every batch is failure-isolated — telemetry must
// never take a read surface down with it.
//
// DIFFERENCE FROM MemoryUsageRecorder, ON PURPOSE: memory's channel also drops on overflow, but
// drops SILENTLY. A telemetry surface that loses rows without saying so is unfalsifiable — a low
// counter and a dropped counter look identical, and every deletion decision taken on that number
// is then unsound. Here a drop increments `DroppedEvents` (readable at any time) and logs a
// warning the first time and then on each power-of-ten, so a burst is visible in the log without
// the log itself becoming the next overflow.
public sealed class TaskUsageRecorder : ITaskUsageRecorder, IAsyncDisposable
{
	abstract record Event;
	// Opened = an engagement (tasks_node_get). For a surface (Opened=false), Deliberate splits an
	// intentional read from an automatic machine pull; irrelevant for an Opened hit.
	sealed record Hit(string Project, string Board, string NodeId, bool Opened, bool Deliberate) : Event;
	// One delivered row. Ts is stamped at ENQUEUE (the moment of the read), not at drain — the
	// event dates the delivery, not the background write.
	sealed record Delivery(string Project, DateTime Ts, TaskDeliveryEvent E) : Event;
	sealed record FlushMark(TaskCompletionSource Done) : Event;

	// Same bound and same drop policy as memory's recorder: back-pressure on a read path would
	// trade a statistic for a latency spike, which is the wrong trade for telemetry.
	const int Capacity = 10_000;

	readonly IScopedDbFactory<TasksDb> _factory;
	// The board CATALOG (core.db) — where the declared role lives. Reached through the SINGLETON
	// ICoreDbFactory rather than the SCOPED ITaskBoardStore: this recorder is a singleton with a
	// background drain loop that outlives every request scope, and capturing a scoped store in it
	// is a captive dependency (its own architecture gate, CaptiveDependencyTests).
	readonly ICoreDbFactory _core;
	readonly ILogger<TaskUsageRecorder>? _logger;
	// THE DROP MUST BE OBSERVED THROUGH THE CALLBACK, not through TryWrite's return value. Under
	// FullMode.DropWrite a full channel discards the incoming item and TryWrite still returns
	// TRUE — which is precisely how memory's recorder loses events without noticing, and why a
	// first attempt at this counter measured zero drops while tens of thousands were discarded.
	// `itemDropped` is the only hook that actually fires on the loss.
	readonly Channel<Event> _events;
	readonly Task _drain;
	// Declared role per (project, board), resolved on the DRAIN thread (never on the read path)
	// and cached for RoleTtl. A role is a rarely-changed declaration; re-reading the board
	// catalog once per delivered row would put a core-db round trip behind every search result.
	readonly ConcurrentDictionary<string, (string Role, DateTime At)> _roles = new(StringComparer.Ordinal);
	static readonly TimeSpan RoleTtl = TimeSpan.FromMinutes(5);
	long _dropped;

	public TaskUsageRecorder(IScopedDbFactory<TasksDb> factory, ICoreDbFactory core,
		ILogger<TaskUsageRecorder>? logger = null)
	{
		_factory = factory;
		_core = core;
		_logger = logger;
		_events = Channel.CreateBounded<Event>(
			new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropWrite },
			itemDropped: OnDropped);
		_drain = Task.Run(DrainLoopAsync);
	}

	public long DroppedEvents => Interlocked.Read(ref _dropped);

	public void Surfaced(string projectKey, string board, IReadOnlyList<string> nodeIds, bool deliberate = true)
	{
		foreach (var nodeId in nodeIds)
			Enqueue(new Hit(projectKey, board, nodeId, Opened: false, Deliberate: deliberate));
	}

	public void Opened(string projectKey, string board, string nodeId) =>
		Enqueue(new Hit(projectKey, board, nodeId, Opened: true, Deliberate: true));

	public void Delivered(string projectKey, IReadOnlyList<TaskDeliveryEvent> events)
	{
		var now = DateTime.UtcNow; // one timestamp per DELIVERY: the rows of one answer share it
		foreach (var e in events)
			Enqueue(new Delivery(projectKey, now, e));
	}

	// The ONE enqueue point. A REFUSED write (the channel is completed — shutdown) is not an
	// overflow and is not counted here; an OVERFLOW arrives at OnDropped instead.
	void Enqueue(Event e) => _events.Writer.TryWrite(e);

	// Called by the channel for each item discarded on overflow. A drop is not an error — it is
	// the deliberate back-pressure choice, because blocking a read path to save a statistic is the
	// wrong trade — but it must never be INVISIBLE: a counter that silently undercounts is one
	// every conclusion drawn from it is unsound on.
	void OnDropped(Event dropped)
	{
		// A flush mark carries no measurement; losing one would cost a WAITER, not a number, so
		// complete it here rather than counting it — a flush must never hang on an overflow.
		if (dropped is FlushMark mark)
		{
			mark.Done.TrySetResult();
			return;
		}

		var count = Interlocked.Increment(ref _dropped);
		// First drop, then 10th, 100th, … — enough to see a burst start and how big it got,
		// without the overflow log becoming its own flood.
		if (IsPowerOfTen(count))
			_logger?.LogWarning(
				"task usage telemetry dropped {Dropped} event(s) — the bounded channel (capacity {Capacity}) is full; " +
				"counters and cost/fit for this period UNDERCOUNT",
				count, Capacity);
	}

	static bool IsPowerOfTen(long n)
	{
		while (n >= 10 && n % 10 == 0) n /= 10;
		return n == 1;
	}

	public async Task FlushAsync(CancellationToken ct = default)
	{
		var mark = new FlushMark(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
		// A mark that cannot be enqueued (the channel is completed) leaves nothing to wait for; a
		// mark DROPPED on overflow is completed by OnDropped, so a flush can never hang on one.
		if (!_events.Writer.TryWrite(mark)) return;
		await mark.Done.Task.WaitAsync(ct);
	}

	async Task DrainLoopAsync()
	{
		await foreach (var e in _events.Reader.ReadAllAsync())
		{
			switch (e)
			{
				case Hit hit:
					try
					{
						Apply(hit);
					}
					catch (Exception ex)
					{
						// Board deleted mid-flight, file locked, … — drop the increment.
						if (_logger?.IsEnabled(LogLevel.Debug) == true)
							_logger.LogDebug(ex, "usage increment dropped for {Project}/{Board}/{NodeId}",
								hit.Project, hit.Board, hit.NodeId);
					}
					break;
				case Delivery delivery:
					try
					{
						await ApplyAsync(delivery);
					}
					catch (Exception ex)
					{
						// Same failure isolation as a counter increment: a lost event loses statistics.
						if (_logger?.IsEnabled(LogLevel.Debug) == true)
							_logger.LogDebug(ex, "delivery event dropped for {Project}/{Board}/{NodeId}",
								delivery.Project, delivery.E.Board, delivery.E.NodeId);
					}
					break;
				case FlushMark mark:
					mark.Done.TrySetResult();
					break;
			}
		}
	}

	void Apply(Hit hit)
	{
		// One file per project; the counter key is (Board, NodeId) — see M022.
		using var db = _factory.NewEnsuredConnection(hit.Project);
		db.Execute("""
			INSERT INTO node_usage (Board, NodeId, SurfacedCount, DeliberateCount, OpenedCount, LastHitAt)
			VALUES (@board, @nodeId, @surfaced, @deliberate, @opened, @at)
			ON CONFLICT(Board, NodeId) DO UPDATE SET
				SurfacedCount = SurfacedCount + excluded.SurfacedCount,
				DeliberateCount = DeliberateCount + excluded.DeliberateCount,
				OpenedCount = OpenedCount + excluded.OpenedCount,
				LastHitAt = excluded.LastHitAt;
			""",
			new DataParameter("board", hit.Board),
			new DataParameter("nodeId", hit.NodeId),
			new DataParameter("surfaced", hit.Opened ? 0 : 1),
			// Deliberate is the honest subset of a surface: only an intentional (non-machine)
			// read counts; an Opened hit is an engagement, not a surface.
			new DataParameter("deliberate", !hit.Opened && hit.Deliberate ? 1 : 0),
			new DataParameter("opened", hit.Opened ? 1 : 0),
			new DataParameter("at", DateTime.UtcNow));
	}

	// Append-only: one row per delivered node (M022). Insert through the linq2db mapping so the
	// columns cannot silently drift from the TaskDeliveryEvent record.
	async Task ApplyAsync(Delivery d)
	{
		var role = await RoleAsync(d.Project, d.E.Board);
		using var db = _factory.NewEnsuredConnection(d.Project);
		db.Insert(new NodeDeliveryEvent
		{
			Ts = d.Ts,
			SessionId = d.E.SessionId,
			Tool = d.E.Tool,
			Board = d.E.Board,
			NodeId = d.E.NodeId,
			Key = d.E.Key,
			DeclaredRole = role,
			DeliveredChars = d.E.DeliveredChars,
			BodyChars = d.E.BodyChars,
			RowChars = d.E.RowChars,
			Rank = d.E.Rank,
			ScoreRaw = d.E.ScoreRaw,
			KRel = d.E.KRel,
			UsageSource = d.E.UsageSource,
		});
	}

	// The board's declared role, resolved off the hot path and memoized. A board that has since
	// been deleted (or was never in the catalog) resolves to `corpus` — the conservative default,
	// never null and never an exception: an unmeasurable delivery is worse than a defaulted one.
	async Task<string> RoleAsync(string project, string board)
	{
		var cacheKey = project + "\x1f" + board;
		if (_roles.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < RoleTtl)
			return hit.Role;
		var role = BoardDeclaredRole.Corpus;
		try
		{
			using var db = _core.Open();
			var declared = await db.TaskBoards
				.Where(b => b.ProjectKey == project && b.Name == board)
				.Select(b => b.DeclaredRole)
				.FirstOrDefaultAsync();
			role = BoardDeclaredRole.Normalize(declared);
		}
		catch (Exception ex)
		{
			if (_logger?.IsEnabled(LogLevel.Debug) == true)
				_logger.LogDebug(ex, "declared role lookup failed for {Project}/{Board}; defaulting to corpus", project, board);
		}

		_roles[cacheKey] = (role, DateTime.UtcNow);
		return role;
	}

	public async ValueTask DisposeAsync()
	{
		_events.Writer.TryComplete();
		try { await _drain.WaitAsync(TimeSpan.FromSeconds(5)); }
		catch (TimeoutException) { /* shutdown must not hang on telemetry */ }
	}
}
