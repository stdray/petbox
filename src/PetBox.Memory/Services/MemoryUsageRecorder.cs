using System.Threading.Channels;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;

namespace PetBox.Memory.Services;

// The one writer of entry_usage. Singleton: increments are enqueued onto a bounded
// channel and drained by a background loop, so the read path that reports a hit never
// waits on SQLite (spec: учёт не замедляет чтение). The channel drops on overflow and
// every batch is failure-isolated per (project, store) — telemetry must never take a
// read surface down with it.
public sealed class MemoryUsageRecorder : IMemoryUsageRecorder, IAsyncDisposable
{
	abstract record Event;
	// Opened = an engagement (memory_get). For a surface (Opened=false), Deliberate splits an
	// intentional search from an automatic machine pull; irrelevant for an Opened hit.
	sealed record Hit(string Project, string Store, string Key, bool Opened, bool Deliberate) : Event;
	sealed record FlushMark(TaskCompletionSource Done) : Event;

	readonly IScopedDbFactory<MemoryDb> _factory;
	readonly ILogger<MemoryUsageRecorder>? _logger;
	readonly Channel<Event> _events = Channel.CreateBounded<Event>(
		new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite });
	readonly Task _drain;

	public MemoryUsageRecorder(IScopedDbFactory<MemoryDb> factory, ILogger<MemoryUsageRecorder>? logger = null)
	{
		_factory = factory;
		_logger = logger;
		_drain = Task.Run(DrainLoopAsync);
	}

	public void Surfaced(string projectKey, string store, IReadOnlyList<string> keys, bool deliberate = true)
	{
		foreach (var key in keys)
			_events.Writer.TryWrite(new Hit(projectKey, store, key, Opened: false, Deliberate: deliberate));
	}

	public void Opened(string projectKey, string store, string key) =>
		_events.Writer.TryWrite(new Hit(projectKey, store, key, Opened: true, Deliberate: true));

	public async Task FlushAsync(CancellationToken ct = default)
	{
		var mark = new FlushMark(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
		if (!_events.Writer.TryWrite(mark)) return; // channel completed/full — nothing to wait for
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
						// Store deleted mid-flight, file locked, … — drop the increment.
						if (_logger?.IsEnabled(LogLevel.Debug) == true)
							_logger.LogDebug(ex, "usage increment dropped for {Project}/{Store}/{Key}",
								hit.Project, hit.Store, hit.Key);
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
		// GetDb (not NewConnection): the migration that creates entry_usage must have run
		// for this file before the raw upsert (reference: NewConnection ≠ migrations).
		var db = _factory.GetDb(hit.Project, hit.Store);
		db.Execute("""
			INSERT INTO entry_usage (Key, SurfacedCount, DeliberateCount, OpenedCount, LastHitAt)
			VALUES (@key, @surfaced, @deliberate, @opened, @at)
			ON CONFLICT(Key) DO UPDATE SET
				SurfacedCount = SurfacedCount + excluded.SurfacedCount,
				DeliberateCount = DeliberateCount + excluded.DeliberateCount,
				OpenedCount = OpenedCount + excluded.OpenedCount,
				LastHitAt = excluded.LastHitAt;
			""",
			new DataParameter("key", hit.Key),
			new DataParameter("surfaced", hit.Opened ? 0 : 1),
			// Deliberate is the honest subset of a surface: only an intentional (non-machine)
			// search counts; an Opened hit is an engagement, not a surface.
			new DataParameter("deliberate", !hit.Opened && hit.Deliberate ? 1 : 0),
			new DataParameter("opened", hit.Opened ? 1 : 0),
			new DataParameter("at", DateTime.UtcNow));
	}

	public async ValueTask DisposeAsync()
	{
		_events.Writer.TryComplete();
		try { await _drain.WaitAsync(TimeSpan.FromSeconds(5)); }
		catch (TimeoutException) { /* shutdown must not hang on telemetry */ }
	}
}
