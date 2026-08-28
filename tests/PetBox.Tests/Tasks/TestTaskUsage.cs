using PetBox.Tasks.Contract;

namespace PetBox.Tests.Tasks;

// Null-object usage telemetry for the task tool tests that are NOT about usage — the same role
// NoopUsageRecorder plays on the memory side. Every tasks_search / tasks_node_get / tasks_board_list
// call now takes the recorder (and the search/list verbs the reader), because recording an
// impression is part of what those verbs DO; a test about cursors or body budgets should not have
// to stand up a database-backed telemetry writer to say so.
//
// It is a NO-OP, never a stub that records into memory: a test that wants to assert on counters
// must use the REAL TaskUsageRecorder against a real file (see TaskUsageTests), or it is asserting
// on a fake and proving nothing about the code that ships.
public sealed class NoopTaskUsageRecorder : ITaskUsageRecorder
{
	public long DroppedEvents => 0;
	public void Surfaced(string projectKey, string board, IReadOnlyList<string> nodeIds, bool deliberate = true) { }
	public void Opened(string projectKey, string board, string nodeId) { }
	public void Delivered(string projectKey, IReadOnlyList<TaskDeliveryEvent> events) { }
	public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class NoopTaskUsageReader : ITaskUsageReader
{
	public Task<IReadOnlyDictionary<string, NodeUsageView>> GetUsageAsync(string projectKey, string board,
		IReadOnlyCollection<string>? nodeIds = null, CancellationToken ct = default) =>
		Task.FromResult<IReadOnlyDictionary<string, NodeUsageView>>(
			new Dictionary<string, NodeUsageView>(StringComparer.Ordinal));

	public Task<BoardUsageAggregate> GetBoardUsageAsync(string projectKey, string board,
		int deadTailLimit = 10, TimeSpan? window = null, CancellationToken ct = default) =>
		Task.FromResult(new BoardUsageAggregate(board, PetBox.Core.Models.BoardDeclaredRole.Corpus,
			0, 0, 0, 0, 0, 0, null, new BoardDeadTail(0, []),
			new BoardUsageCost(30, 0, 0, 0, null, 0)));
}

// One shared instance of each — they hold no state, so a per-call `new` would only be noise at
// 60-odd call sites.
public static class NoopTaskUsage
{
	public static readonly ITaskUsageRecorder Recorder = new NoopTaskUsageRecorder();
	public static readonly ITaskUsageReader Reader = new NoopTaskUsageReader();
}
