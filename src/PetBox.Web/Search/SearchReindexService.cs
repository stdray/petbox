using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Data;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Web.Search;

// Which tiers a reindex touches. Memory and tasks keep their Class-B state in DIFFERENT files with
// DIFFERENT cursor-naming conventions, so the tier is an explicit choice, not a wildcard.
public enum ReindexTier
{
	Memory,
	Tasks,
	All,
}

// What one tier's reset did — and the numbers to VERIFY the backfill against afterwards.
// `ActiveDocs` is how many rows the drain should end up with in `search_vec`; `VectorRows`/`Dead`
// are read AFTER the reset (so Dead is 0 by construction, and VectorRows is the pre-backfill
// baseline — the "0 vectors for N documents" that motivated the reindex). `LexicalReset` is the
// CLASS-A half (reindex-as-first-class-mechanism): how many lexical projection markers (memory:
// one per store; tasks: the one project-wide marker) were rewound to 0 — each rewound marker makes
// EnsureLexicalBackfillAsync rebuild that scope's search_fts rows on the very next search, no
// enrichment drain involved (Class-A is synchronous, not a background job).
public sealed record ReindexTierResult(
	string Tier,
	IReadOnlyList<string> Indexes,
	long ActiveDocs,
	long VectorRows,
	int DeadCleared,
	int CursorsReset,
	int LexicalReset);

public sealed record SearchReindexResult(string ProjectKey, IReadOnlyList<ReindexTierResult> Tiers)
{
	public long TotalDocsToEmbed => Tiers.Sum(t => t.ActiveDocs);
}

// REINDEX = resurrect a project's semantic index from zero.
//
// The disease it cures: while a project had no Embed route, every drain pass charged an attempt
// against innocent documents until they were dead-lettered (Dead=1 → AsyncVectorizationWorker skips
// them FOREVER) — and, because a dead-lettered item no longer blocks, the cursor advanced PAST them.
// Fixing the route heals nothing on its own: those docs are blacklisted AND behind the cursor, so
// they are not even in the delta. (The poison/infrastructure split now prevents this from recurring;
// this service repairs the damage already on disk.)
//
// The cure is both halves, in one shot, per index:
//   DELETE search_deadletter  → the condemned docs are eligible again;
//   UPDATE search_cursor = 0  → the next DeltaAsync(0) yields the WHOLE store as `added`.
// Then nothing else happens here: the STOCK drain (SearchEnrichmentService, 60s) does the work,
// in take-N portions (MaxDocsPerPass), through the same worker, the same gates, the same logs.
// Idempotent by construction — running it twice just deletes nothing and sets an already-0 cursor.
//
// CLASS-A, alongside (reindex-as-first-class-mechanism): the SAME rewind-to-0 move against the
// lexical projection marker(s) (memory: MemoryCursors.Lexical per store; tasks:
// TasksCursors.Lexical, one per file) — no drain to wait for here, EnsureLexicalBackfillAsync
// rebuilds search_fts synchronously on the very next search. This is what makes a projection
// change (ToDoc growing a new field, say) an operator button instead of an empty-the-table
// migration: bump *SearchDocs.LexicalProjectionVersion in code, and any file whose marker is
// still behind self-heals on its own; `search_reindex` exists for the "I don't want to wait for
// the next incidental search" case.
//
// The GATE: it refuses outright when Embed is not available for the project (no route, breaker
// open). The worker would no longer burn the docs (it stalls instead), but starting a full backfill
// into a dead endpoint is still pointless: it would only reset the state and sit there, and the
// operator would be left believing a reindex is under way. (The lexical half needs no such gate —
// it is local and synchronous — but it shares the same refusal so ONE tool call either resets
// everything or nothing, never a half-reset an operator has to reason about.)
public sealed partial class SearchReindexService
{
	readonly IScopedDbFactory<MemoryDb> _memory;
	readonly IScopedDbFactory<TasksDb> _tasks;
	readonly IProjectCatalog _catalog;
	readonly ILlmClient? _llm;
	readonly ILogger<SearchReindexService>? _logger;

	public SearchReindexService(
		IScopedDbFactory<MemoryDb> memory, IScopedDbFactory<TasksDb> tasks, IProjectCatalog catalog,
		ILlmClient? llm = null, ILogger<SearchReindexService>? logger = null)
	{
		_memory = memory;
		_tasks = tasks;
		_catalog = catalog;
		_llm = llm;
		_logger = logger;
	}

	public async Task<SearchReindexResult> ReindexAsync(
		string projectKey, ReindexTier tier = ReindexTier.All, CancellationToken ct = default)
	{
		// Gate FIRST, before a single row is touched: refusing must leave the state exactly as it was.
		if (_llm is null || !await _llm.IsAvailableAsync(projectKey, LlmCapability.Embed, ct))
			throw new InvalidOperationException(
				$"reindex refused: the Embed capability is not available for project '{projectKey}' " +
				"(no route configured, or every endpoint's circuit breaker is open). Nothing was reset — " +
				"fix the LLM route first, then reindex.");

		var tiers = new List<ReindexTierResult>();
		if (tier is ReindexTier.Memory or ReindexTier.All &&
			(await _catalog.ListMemoryProjectKeysAsync(ct)).Contains(projectKey, StringComparer.Ordinal))
			tiers.Add(await ResetMemoryAsync(projectKey, ct));
		if (tier is ReindexTier.Tasks or ReindexTier.All &&
			(await _catalog.ListTaskProjectKeysAsync(ct)).Contains(projectKey, StringComparer.Ordinal))
			tiers.Add(await ResetTasksAsync(projectKey, ct));

		var result = new SearchReindexResult(projectKey, tiers);
		if (_logger is not null)
			foreach (var t in tiers)
				LogReindexed(_logger, projectKey, t.Tier, t.Indexes.Count, t.DeadCleared, t.CursorsReset,
					t.VectorRows, t.ActiveDocs, t.LexicalReset);
		return result;
	}

	// Memory: one file per project, all stores inside; the vector cursor of a store is
	// `vector:{store}` (MemoryCursors.Vector) — the SAME table also holds the memory jobs' own
	// markers (dedup sweep, behavior mining), which is exactly why only the enumerated names go.
	// The lexical marker (`lexical:{store}`, MemoryCursors.Lexical) is reset in a SEPARATE
	// SearchIndexReset call so its count doesn't get folded into the Class-B `Indexes`/`cursors`
	// the caller verifies the drain against — a lexical rewind has no drain to watch.
	async Task<ReindexTierResult> ResetMemoryAsync(string projectKey, CancellationToken ct)
	{
		using var db = _memory.NewEnsuredConnection(projectKey);
		// Every store the vectorization job walks (it derives its store list from Entries the same way).
		var stores = await db.Entries.Select(e => e.Store).Distinct().ToListAsync(ct);
		var indexes = stores.Select(MemoryCursors.Vector).ToList();
		var (dead, cursors) = await SearchIndexReset.ResetAsync(db, indexes, ct);
		var (_, lexicalReset) = await SearchIndexReset.ResetAsync(db, stores.Select(MemoryCursors.Lexical).ToList(), ct);
		var active = await db.Entries.Where(e => e.ActiveTo == null).LongCountAsync(ct);
		var (vectors, _) = await SearchIndexStatsReader.ReadAsync(db, ct);
		return new ReindexTierResult("memory", indexes, active, vectors, dead, cursors, lexicalReset);
	}

	// Tasks: one file per project, all boards inside; the vector cursor of a board is the BARE board
	// name (TasksVectorizationJob) — no prefix, so the enumerated-names rule matters even more here.
	// ActiveDocs counts every live, identity-bearing node (TasksSearchDocs.IsIndexable) — since
	// search-hides-terminal-nodes that includes terminal nodes too, which is what the index carries.
	// The lexical marker is ONE row for the whole file (TasksCursors.Lexical) — nodes and comments
	// share the single TasksSearchDocs projection, so there is nothing to enumerate per board.
	async Task<ReindexTierResult> ResetTasksAsync(string projectKey, CancellationToken ct)
	{
		using var db = _tasks.NewEnsuredConnection(projectKey);
		var boards = await db.GetTable<PlanNode>().Where(n => n.ActiveTo == null)
			.Select(n => n.Board).Distinct().ToListAsync(ct);
		var (dead, cursors) = await SearchIndexReset.ResetAsync(db, boards, ct);
		var (_, lexicalReset) = await SearchIndexReset.ResetAsync(db, [TasksCursors.Lexical], ct);
		var open = await db.GetTable<PlanNode>().Where(n => n.ActiveTo == null).ToListAsync(ct);
		var active = open.LongCount(TasksSearchDocs.IsIndexable);
		var (vectors, _) = await SearchIndexStatsReader.ReadAsync(db, ct);
		return new ReindexTierResult("tasks", boards, active, vectors, dead, cursors, lexicalReset);
	}

	[LoggerMessage(EventId = 414, Level = LogLevel.Warning,
		Message = "search reindex {Project}/{Tier}: {Indexes} index(es) reset — cleared {DeadCleared} dead-letter row(s), rewound {CursorsReset} cursor(s) to 0; search_vec had {VectorRows} row(s), {ActiveDocs} document(s) will be re-embedded by the enrichment drain; rewound {LexicalReset} lexical projection marker(s) — search_fts rebuilds on the next search")]
	static partial void LogReindexed(ILogger logger, string project, string tier, int indexes, int deadCleared,
		int cursorsReset, long vectorRows, long activeDocs, int lexicalReset);
}
