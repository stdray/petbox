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

	// work drain-advanced-flag-does-not-mean-advanced: `Advanced` only ever meant "cursor written,
	// nothing blocked" — it stays true even when the written value is IDENTICAL to what was already
	// there (a real month-long incident: 40k+ passes reported Advanced=true while the cursor sat
	// still). `Moved` is the honest complement: did the cursor's VALUE actually change this pass.
	// Two drains, no change to the source between them: pass 1 has a real delta to write (Moved
	// true), pass 2 re-derives the SAME cursor value from an already-drained source (Moved false) —
	// yet Advanced is true both times, because neither pass was blocked.
	[Fact]
	public async Task TwoDrainsNoSourceChange_FirstReportsMoved_SecondDoesNot_WhileAdvancedStaysTrue()
	{
		var source = new FakeSource { Upserts = [Doc("a")], Version = 5 };
		var index = new FakeIndex();
		var worker = new AsyncVectorizationWorker(IndexName, source, index, new InMemoryIndexCursorStore());

		var first = await worker.DrainAsync();
		first.Advanced.Should().BeTrue();
		first.Moved.Should().BeTrue("cursor went 0 -> 5");
		first.Cursor.Should().Be(5);

		// Nothing changed in the source (Version still 5); the second pass sees an empty delta whose
		// CurrentVersion is still 5, so it re-writes the exact same cursor value.
		var second = await worker.DrainAsync();
		second.Advanced.Should().BeTrue("nothing blocked the pass");
		second.Moved.Should().BeFalse("cursor was already 5 — this write didn't move it");
		second.Cursor.Should().Be(5);
	}

	sealed record LogEntry(MsLogLevel Level, int EventId, string Message);

	sealed class CapturingLogger : ILogger
	{
		public List<LogEntry> Entries { get; } = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(MsLogLevel logLevel) => true;
		public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			Entries.Add(new LogEntry(logLevel, eventId.Id, formatter(state, exception)));
	}

	// The record field alone isn't enough — EventId 404 is the only diagnostic window into a stuck
	// drain (see the card body). `Moved` must reach the actual log TEXT, not just DrainResult.
	[Fact]
	public async Task LogDrain_EventId404_TextCarriesMoved()
	{
		var source = new FakeSource { Upserts = [Doc("a")], Version = 5 };
		var index = new FakeIndex();
		var log = new CapturingLogger();
		var worker = new AsyncVectorizationWorker(IndexName, source, index, new InMemoryIndexCursorStore(), log: log);

		await worker.DrainAsync();

		var entry = log.Entries.Should().ContainSingle(e => e.EventId == 404).Subject;
		entry.Message.Should().Contain("advanced True");
		entry.Message.Should().Contain("moved True");
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
