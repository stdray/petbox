using LinqToDB.Data;
using Microsoft.Extensions.Logging;
using PetBox.Core.Search;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace PetBox.Tests.Search;

// The async-vectorization worker: the write path never blocks on embedding (the worker is the
// only thing that touches the Class-B index), an embedder outage stalls the cursor and recovers
// by backfilling with no lost writes, and a poison item is dead-lettered so it can't
// head-of-line-block the cursor. Fake source + fake index + in-memory cursor store.
public sealed class AsyncVectorizationWorkerTests
{
	const string Scope = "proj/notes";
	const string IndexName = "vec";

	static SearchDoc Doc(string id) => new(Scope, "note", id, id + " text");

	[Fact]
	public async Task HappyDrain_IndexesAll_AndAdvancesCursor()
	{
		var source = new FakeSource { Upserts = [Doc("a"), Doc("b")], Version = 5 };
		var index = new FakeIndex();
		var worker = new AsyncVectorizationWorker(IndexName, source, index, new InMemoryIndexCursorStore());

		var r = await worker.DrainAsync();

		r.Indexed.Should().Be(2);
		r.Advanced.Should().BeTrue();
		r.Cursor.Should().Be(5);
		index.Indexed.Select(x => x.Id).Should().BeEquivalentTo(["a", "b"]);
	}

	[Fact]
	public async Task EmbedderDown_HoldsCursor_ThenBackfillsOnRecovery()
	{
		var source = new FakeSource { Upserts = [Doc("a"), Doc("b")], Version = 5 };
		var index = new FakeIndex { FailAll = true };
		var store = new InMemoryIndexCursorStore();
		var worker = new AsyncVectorizationWorker(IndexName, source, index, store);

		var down = await worker.DrainAsync();
		down.Indexed.Should().Be(0);
		down.Advanced.Should().BeFalse();
		down.Cursor.Should().Be(0); // cursor held → the delta is still pending

		index.FailAll = false; // embedder recovers
		var up = await worker.DrainAsync();
		up.Indexed.Should().Be(2); // same delta drains forward — nothing lost
		up.Advanced.Should().BeTrue();
		up.Cursor.Should().Be(5);
	}

	[Fact]
	public async Task PoisonItem_IsDeadLettered_AndUnblocksCursor()
	{
		var source = new FakeSource { Upserts = [Doc("good"), Doc("bad")], Version = 5 };
		var index = new FakeIndex { FailIds = ["bad"] };
		var store = new InMemoryIndexCursorStore();
		var worker = new AsyncVectorizationWorker(IndexName, source, index, store, maxAttempts: 2);

		var first = await worker.DrainAsync();
		first.Advanced.Should().BeFalse(); // "bad" still transiently failing → cursor held
		first.Cursor.Should().Be(0);

		var second = await worker.DrainAsync();
		second.DeadLettered.Should().Be(1); // "bad" burned through maxAttempts → dead-lettered
		second.Advanced.Should().BeTrue(); // no transient failures remain → cursor advances
		second.Cursor.Should().Be(5);

		(await store.IsDeadAsync(IndexName, "note", "bad")).Should().BeTrue();
		index.Indexed.Select(x => x.Id).Should().Contain("good"); // the healthy item got through

		// A dead-lettered item is skipped on subsequent drains (no further attempts).
		source.Version = 6; // a new delta arrives
		var third = await worker.DrainAsync();
		index.Indexed.Count(x => x.Id == "bad").Should().Be(0);
		third.Advanced.Should().BeTrue();
	}

	[Fact]
	public async Task Delete_Propagates()
	{
		var source = new FakeSource { Deletes = [new DocRef(Scope, "note", "gone")], Version = 3 };
		var index = new FakeIndex();
		var worker = new AsyncVectorizationWorker(IndexName, source, index, new InMemoryIndexCursorStore());

		var r = await worker.DrainAsync();

		r.Deleted.Should().Be(1);
		r.Advanced.Should().BeTrue();
		index.Deleted.Should().ContainSingle().Which.Should().Be(("note", "gone"));
	}

	// ---- fakes ----

	sealed class FakeSource : ISearchSource
	{
		public List<SearchDoc> Upserts = [];
		public List<DocRef> Deletes = [];
		public long Version = 1;

		public Task<SourceDelta> DeltaAsync(long sinceVersion, CancellationToken ct = default) =>
			Task.FromResult(sinceVersion >= Version
				? new SourceDelta([], [], Version)
				: new SourceDelta(Upserts, Deletes, Version));
	}

	sealed class FakeIndex : ISearchIndex
	{
		public bool FailAll;
		public HashSet<string> FailIds = [];
		public List<(string Type, string Id)> Indexed = [];
		public List<(string Type, string Id)> Deleted = [];

		public SearchConsistency ConsistencyClass => SearchConsistency.Eventual;
		public SearchCapability Capability => SearchCapability.Vector;

		public Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default)
		{
			if (FailAll || FailIds.Contains(doc.Id)) throw new InvalidOperationException("embed down");
			Indexed.Add((doc.Type, doc.Id));
			return Task.CompletedTask;
		}

		public Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default)
		{
			Deleted.Add((type, id));
			return Task.CompletedTask;
		}

		public Task DeleteByTypeAsync(DataConnection? tx, string scope, string type, CancellationToken ct = default) =>
			Task.CompletedTask;

		public Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<Hit>>([]);
	}
}

// work vectorization-drain-log-gate: LogDrain (EventId 404) used to be fixed at Information and
// only skipped calling entirely when the pass was totally empty. Measured 2026-08-28: 96.7% of its
// daily volume (5118/5294) was a handful of store/board partitions reporting Deleted>0 with the
// SAME count and a never-advancing cursor on EVERY tick for 25 straight hours — Deleted alone is
// not reliable signal. The fix mirrors MemoryVectorizationJob/TasksVectorizationJob's existing
// hasSignal criterion (EventId 410/411): Information only for Indexed>0, DeadLettered>0, or Lag>0;
// everything else (including a deletes-only pass) goes to Debug, which the self-log's global
// MinimumLevel=Information then drops. These tests pin both halves of the gate.
public sealed class AsyncVectorizationDrainLogGateTests
{
	const string Scope = "proj/notes";
	const string IndexName = "vec";

	static SearchDoc Doc(string id) => new(Scope, "note", id, id + " text");

	sealed record Entry(MsLogLevel Level, int EventId, string Message);

	sealed class CapturingLogger : ILogger
	{
		public List<Entry> Entries { get; } = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(MsLogLevel logLevel) => true;
		public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			Entries.Add(new Entry(logLevel, eventId.Id, formatter(state, exception)));
	}

	[Fact]
	public async Task DeletesOnly_NoOtherSignal_GatesToDebug()
	{
		var source = new LocalFakeSource { Deletes = [new DocRef(Scope, "note", "gone")], Version = 3 };
		var index = new LocalFakeIndex();
		var log = new CapturingLogger();
		var worker = new AsyncVectorizationWorker(IndexName, source, index, new InMemoryIndexCursorStore(), log: log);

		var r = await worker.DrainAsync();

		r.Deleted.Should().Be(1);
		r.Indexed.Should().Be(0);
		r.DeadLettered.Should().Be(0);
		r.Lag.Should().Be(0);
		var entry = log.Entries.Should().ContainSingle(e => e.EventId == 404).Subject;
		entry.Level.Should().Be(MsLogLevel.Debug, "a deletes-only pass with no lag/dead-letter is not a signal worth Information");
		entry.Message.Should().Contain("deleted 1"); // still queryable at Debug, not silenced entirely
	}

	[Fact]
	public async Task Indexed_StaysAtInformation()
	{
		var source = new LocalFakeSource { Upserts = [Doc("a"), Doc("b")], Version = 5 };
		var index = new LocalFakeIndex();
		var log = new CapturingLogger();
		var worker = new AsyncVectorizationWorker(IndexName, source, index, new InMemoryIndexCursorStore(), log: log);

		await worker.DrainAsync();

		var entry = log.Entries.Should().ContainSingle(e => e.EventId == 404).Subject;
		entry.Level.Should().Be(MsLogLevel.Information, "Indexed=2 is real work");
	}

	[Fact]
	public async Task DeadLettered_StaysAtInformation()
	{
		var source = new LocalFakeSource { Upserts = [Doc("bad")], Version = 5 };
		var index = new LocalFakeIndex { FailIds = ["bad"] };
		var store = new InMemoryIndexCursorStore();
		var log = new CapturingLogger();
		var worker = new AsyncVectorizationWorker(IndexName, source, index, store, maxAttempts: 1, log: log);

		var r = await worker.DrainAsync();

		r.DeadLettered.Should().Be(1);
		var entry = log.Entries.Should().ContainSingle(e => e.EventId == 404).Subject;
		entry.Level.Should().Be(MsLogLevel.Information, "a dead-letter must never be gated to Debug");
	}

	[Fact]
	public async Task LagFromStalledEmbedder_StaysAtInformation()
	{
		// The embedder is down: nothing indexes, nothing deletes, nothing dead-letters — but the
		// cursor is held while the source has moved on, so Lag>0. This is the "problem visible
		// immediately" case the original card required and it must survive this gate untouched.
		var source = new LocalFakeSource { Upserts = [Doc("a"), Doc("b")], Version = 5 };
		var index = new LocalFakeIndex { FailAll = true };
		var log = new CapturingLogger();
		var worker = new AsyncVectorizationWorker(IndexName, source, index, new InMemoryIndexCursorStore(), log: log);

		var r = await worker.DrainAsync();

		r.Indexed.Should().Be(0);
		r.DeadLettered.Should().Be(0);
		r.Lag.Should().BeGreaterThan(0);
		var entry = log.Entries.Should().ContainSingle(e => e.EventId == 404).Subject;
		entry.Level.Should().Be(MsLogLevel.Information, "Lag>0 is a real problem signal, even with Indexed=0");
	}

	// ---- local fakes (mirrors AsyncVectorizationWorkerTests' fakes; kept file-local since those are private) ----

	sealed class LocalFakeSource : ISearchSource
	{
		public List<SearchDoc> Upserts = [];
		public List<DocRef> Deletes = [];
		public long Version = 1;

		public Task<SourceDelta> DeltaAsync(long sinceVersion, CancellationToken ct = default) =>
			Task.FromResult(sinceVersion >= Version
				? new SourceDelta([], [], Version)
				: new SourceDelta(Upserts, Deletes, Version));
	}

	sealed class LocalFakeIndex : ISearchIndex
	{
		public bool FailAll;
		public HashSet<string> FailIds = [];
		public List<(string Type, string Id)> Indexed = [];
		public List<(string Type, string Id)> Deleted = [];

		public SearchConsistency ConsistencyClass => SearchConsistency.Eventual;
		public SearchCapability Capability => SearchCapability.Vector;

		public Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default)
		{
			if (FailAll || FailIds.Contains(doc.Id)) throw new InvalidOperationException("embed down");
			Indexed.Add((doc.Type, doc.Id));
			return Task.CompletedTask;
		}

		public Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default)
		{
			Deleted.Add((type, id));
			return Task.CompletedTask;
		}

		public Task DeleteByTypeAsync(DataConnection? tx, string scope, string type, CancellationToken ct = default) =>
			Task.CompletedTask;

		public Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<Hit>>([]);
	}
}
