using System.Diagnostics;
using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using PetBox.Core.Contract;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Observability;
using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services.Methodology;
using PetBox.Tasks.Services.NodeRef;
using PetBox.Tasks.Services.Search;
using PetBox.Tasks.Services.Upsert;
using PetBox.Tasks.Validation;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Services;

// The one implementation of ITasksService: the single door to the task store. All the
// domain logic that used to live in the MCP tool layer (spec validation, workflow
// application, blocker/spec-link invariants, delivery roll-up, FSM effects) lives here,
// so the MCP tools, Razor pages and REST share exactly one code path into the data.
public sealed partial class TasksService : ITasksService
{
	readonly ITaskBoardStore _boards;
	readonly IRelationStore _relations;
	readonly ITagStore _tags;
	readonly ICommentService _comments;
	// Optional embedding capability (DI auto-fills when an LLM router is registered).
	// Null → semantic search disabled and embed-on-write skipped (lexical-only); never throws.
	readonly ILlmClient? _llm;
	// Relevance re-ranking policy — here only the semantic-noise floor is consumed (query search).
	// Bound from the `Search` config section; defaults are conservative, so an un-wired
	// construction (tests, other adapters) still gets the shipped flooring (same pattern as
	// MemoryService).
	readonly SearchRerankOptions _rerank;
	// Node identity (slug / NodeId) resolution — private collaborator, not DI-registered.
	readonly NodeRefResolver _nodeRefs;
	// Post-write stages for UpsertAsync (associations + FSM/delete effects).
	readonly TaskTransitionEffects _effects;
	readonly TaskUpsertAssociations _associations;
	// Methodology definition storage + live schema migration — private collaborator.
	readonly MethodologyDefinitionService _methodologyDefs;
	// Named methodology templates (independent of live process / instances).
	readonly MethodologyTemplateService _methodologyTemplates;
	// Named methodology instances (live process automata + board membership).
	readonly MethodologyInstanceService _methodologyInstances;

	// Dependency-free declarative invariants (immutable NodeId/type). Static — no state.
	static readonly PlanNodeChangeValidator ChangeValidator = new();

	// MRL truncation dim for the vector index (must match TasksVectorizationJob). The fusion
	// candidate depth is per-request: max(3×limit, 50) — see SearchNodesAsync.
	const int VectorDim = 1024;

	public TasksService(ITaskBoardStore boards, IRelationStore relations, ITagStore tags, ICommentService comments, ILlmClient? llm = null, SearchRerankOptions? rerank = null)
	{
		_boards = boards;
		_relations = relations;
		_tags = tags;
		_comments = comments;
		_llm = llm;
		_rerank = rerank ?? new SearchRerankOptions();
		_nodeRefs = new NodeRefResolver(boards);
		_effects = new TaskTransitionEffects(boards, relations, tags);
		_associations = new TaskUpsertAssociations(boards, relations, tags, _effects);
		_methodologyDefs = new MethodologyDefinitionService(boards);
		_methodologyTemplates = new MethodologyTemplateService(boards, _methodologyDefs, instanceRules: null);
		// instance service needs template source resolution + optional counts for list/get.
		_methodologyInstances = new MethodologyInstanceService(boards, _methodologyTemplates, CountActiveStatusesAsync);
		// Wire instance snapshot source into templates after both exist (circular-safe via delegate).
		_methodologyTemplates.BindInstanceRules(async (projectKey, instanceName, ct) =>
			await _methodologyInstances.GetDefinitionAsync(projectKey, instanceName, allowClosed: true, ct));
	}

	// Status histogram for one board's active nodes (instance list/get summary).
	Task<IReadOnlyDictionary<string, int>> CountActiveStatusesAsync(string projectKey, string board, CancellationToken ct)
	{
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var statuses = ctx.PlanNodes
			.Where(n => n.Board == board && n.ActiveTo == null)
			.Select(n => n.Status)
			.ToList();
		IReadOnlyDictionary<string, int> counts = statuses.GroupBy(s => s, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
		return Task.FromResult(counts);
	}

	// ---- board lifecycle ----

	// Process-role kinds: ≤1 open board per kind INSIDE an instance (not project-wide).
	static readonly BoardKind[] Methodological = [BoardKind.Spec, BoardKind.Ideas, BoardKind.Intake, BoardKind.Work];

	public async Task<TaskBoardMeta> CreateBoardAsync(string projectKey, string board, string? kind, string? description, string? specBoard, string? methodologyInstance = null, CancellationToken ct = default)
	{
		// Membership: required once the project has entered the instance world (any instance
		// exists). Pre-instance projects keep the legacy null-membership path so enable/def
		// and existing boards work until backfill; the MCP surface documents the requirement.
		var instanceName = string.IsNullOrWhiteSpace(methodologyInstance)
			? null
			: methodologyInstance.Trim().ToLowerInvariant();
		if (instanceName is null)
		{
			if (await _methodologyInstances.AnyAsync(projectKey, ct))
				throw new ArgumentException(
					"methodology instance is required — pass methodologyInstance (create one with tasks_methodology_create first); board_create without an instance is rejected once the project has any methodology instance");
		}
		else
		{
			var inst = await _methodologyInstances.GetAsync(projectKey, instanceName, ct)
				?? throw new ArgumentException($"methodology instance '{instanceName}' not found in project '{projectKey}'");
			if (inst.Closed)
				throw new ArgumentException($"methodology instance '{instanceName}' is closed — cannot create boards on a closed instance");
		}

		var kindSlug = (kind ?? "simple").Trim().ToLowerInvariant();
		// Prefer instance rules when membership is set; else project def / presets.
		var runtime = instanceName is not null
			? await RuntimeForInstanceAsync(projectKey, instanceName, ct)
			: await RuntimeAsync(projectKey, ct);
		string canonical;
		if (runtime.IsDefinedKind(kindSlug))
		{
			// A definition-declared kind is stored VERBATIM. Process-role singleton still
			// applies when the kind maps onto a process-role BoardKind.
			canonical = kindSlug;
			if (Enum.TryParse<BoardKind>(kindSlug, ignoreCase: true, out var definedAs) && Methodological.Contains(definedAs))
				await _methodologyInstances.AssertProcessRoleSingletonAsync(projectKey, kindSlug, instanceName, ct: ct);
		}
		else if (Enum.TryParse<BoardKind>(kindSlug, ignoreCase: true, out var k))
		{
			canonical = k.ToString().ToLowerInvariant();
			await _methodologyInstances.AssertProcessRoleSingletonAsync(projectKey, canonical, instanceName, ct: ct);
		}
		else
		{
			throw new ArgumentException(
				$"unknown board kind '{kind}' — valid kinds: {string.Join("|", runtime.KnownKinds())}" +
				" (a custom kind must first be declared via tasks_methodology_def_upsert or the instance's rules)");
		}
		await ValidateSpecBoardAsync(projectKey, canonical, specBoard, ct);
		var meta = await _boards.CreateAsync(projectKey, board, description, canonical, specBoard, instanceName, ct);
		await AutoWireSpecAsync(projectKey, ct); // a fresh spec or work board may complete the link
		return meta;
	}

	public Task<TaskBoardMeta> AdoptBoardAsync(string projectKey, string board, string methodologyInstance, CancellationToken ct = default) =>
		_methodologyInstances.AdoptBoardAsync(projectKey, board, methodologyInstance, ct);

	// Load the project's methodology definition and wrap it as the FSM resolution seam —
	// once per service call (no caching layer; SQLite is local), resolved BEFORE queries
	// are built (the runtime helpers are sync and used inside query predicates). No
	// definition → the presets-only runtime, so preset boards behave exactly as before.
	async Task<MethodologyRuntime> RuntimeAsync(string projectKey, CancellationToken ct)
	{
		var view = await GetMethodologyDefinitionAsync(projectKey, ct);
		return view is null ? MethodologyRuntime.PresetsOnly : new MethodologyRuntime(view.Definition);
	}

	// Board-scoped runtime: instance rules when the board has membership, else project def.
	async Task<MethodologyRuntime> RuntimeForBoardAsync(string projectKey, TaskBoardMeta meta, CancellationToken ct)
	{
		if (!string.IsNullOrWhiteSpace(meta.MethodologyInstance))
			return await RuntimeForInstanceAsync(projectKey, meta.MethodologyInstance, ct);
		return await RuntimeAsync(projectKey, ct);
	}

	async Task<MethodologyRuntime> RuntimeForInstanceAsync(string projectKey, string instanceName, CancellationToken ct)
	{
		var def = await _methodologyInstances.GetDefinitionAsync(projectKey, instanceName, allowClosed: true, ct);
		return def is null ? await RuntimeAsync(projectKey, ct) : new MethodologyRuntime(def);
	}

	// The public door to the resolution seam (same instance the private RuntimeAsync builds
	// for every service call). UI pages hold it for a request to answer kind/terminality/
	// quick-add/next-status the SAME way the MCP path does.
	public Task<MethodologyRuntime> GetRuntimeAsync(string projectKey, CancellationToken ct = default) =>
		RuntimeAsync(projectKey, ct);

	// Data-driven SpecBoard auto-wire (primitives-enum-residual): for every kind that
	// declares AutoWireSpecFrom, when exactly one active board of that kind and one of the
	// target kind exist WITHIN THE SAME INSTANCE membership bucket and SpecBoard is empty,
	// wire them. The quartet work→spec rule is preset DATA on the work KindDef.
	async Task AutoWireSpecAsync(string projectKey, CancellationToken ct)
	{
		var all = (await _boards.ListAsync(projectKey, ct)).Where(b => b.ClosedAt == null).ToList();
		// Group by membership (null = legacy unassigned bucket) so multi-instance projects
		// never cross-wire work→spec across instances.
		foreach (var group in all.GroupBy(b => b.MethodologyInstance ?? "", StringComparer.OrdinalIgnoreCase))
		{
			var runtime = group.Key.Length > 0
				? await RuntimeForInstanceAsync(projectKey, group.Key, ct)
				: await RuntimeAsync(projectKey, ct);
			var active = group.ToList();
			foreach (var kind in runtime.EffectiveKinds())
			{
				if (kind.AutoWireSpecFrom is not { Length: > 0 } fromKind) continue;
				var self = active.Where(b => string.Equals(b.Kind, kind.Kind, StringComparison.OrdinalIgnoreCase)).ToList();
				var target = active.Where(b => string.Equals(b.Kind, fromKind, StringComparison.OrdinalIgnoreCase)).ToList();
				if (self.Count == 1 && target.Count == 1 && string.IsNullOrWhiteSpace(self[0].SpecBoard))
					await _boards.UpdateAsync(projectKey, self[0].Name, m => m with { SpecBoard = target[0].Name }, ct);
			}
		}
	}

	public async Task<(bool Set, string? SpecBoard)> SetSpecBoardAsync(string projectKey, string board, string? specBoard, CancellationToken ct = default)
	{
		await EnsureBoard(projectKey, board, ct);
		var meta = (await _boards.FindAsync(projectKey, board, ct))!;
		await ValidateSpecBoardAsync(projectKey, meta.Kind, specBoard, ct);
		var norm = string.IsNullOrWhiteSpace(specBoard) ? null : specBoard;
		var set = await _boards.UpdateAsync(projectKey, board, m => m with { SpecBoard = norm }, ct);
		return (set, norm);
	}

	public Task<IReadOnlyList<TaskBoardMeta>> ListBoardsAsync(string projectKey, CancellationToken ct = default) =>
		_boards.ListAsync(projectKey, ct);

	public async Task<bool> DeleteBoardAsync(string projectKey, string board, CancellationToken ct = default)
	{
		if (!await _boards.DeleteAsync(projectKey, board, ct)) return false;

		// The board's rows are gone, but its search docs are not: _boards.DeleteAsync bulk-deletes
		// the PlanNodes rows without the per-node FTS/vector hygiene the upsert path runs, so every
		// search_fts/search_vec doc keyed (Scope=project, Type=board) is now orphaned. Left behind,
		// those docs keep matching queries and then crash HybridCandidatesAsync (GetAsync on the
		// vanished board). Purge them board-wide. Vector docs only exist when an embedder was
		// configured — the same `_llm is not null` gate the search path uses.
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var fts = new SqliteFtsIndex(() => ctx);
		using var tx = await ctx.BeginTransactionAsync(ct);
		try
		{
			await fts.DeleteByTypeAsync(ctx, projectKey, board, ct);
			if (_llm is not null)
				await new VectorSearchIndex(() => ctx, new LlmClientEmbedder(_llm, projectKey), VectorDim)
					.DeleteByTypeAsync(ctx, projectKey, board, ct);
			await tx.CommitAsync(ct);
		}
		catch
		{
			await tx.RollbackAsync(ct);
			throw;
		}
		return true;
	}

	public Task<bool> SetClosedAsync(string projectKey, string board, bool closed, CancellationToken ct = default) =>
		_boards.UpdateAsync(projectKey, board, m => m with { ClosedAt = closed ? DateTime.UtcNow : null }, ct);

	public Task<bool> BoardExistsAsync(string projectKey, string board, CancellationToken ct = default) =>
		_boards.ExistsAsync(projectKey, board, ct);

	public async Task<BoardKind> ResolveKindAsync(string projectKey, string board, CancellationToken ct = default)
	{
		await EnsureBoard(projectKey, board, ct);
		return MethodologyPresets.ParseKind((await _boards.FindAsync(projectKey, board, ct))!.Kind);
	}

	public async Task<BoardWorkflowView> GetBoardWorkflowAsync(string projectKey, string board, CancellationToken ct = default)
	{
		await EnsureBoard(projectKey, board, ct);
		var meta = (await _boards.FindAsync(projectKey, board, ct))!;
		var runtime = await RuntimeForBoardAsync(projectKey, meta, ct);
		return new BoardWorkflowView(runtime.KindName(meta.Kind), runtime.Blocks(meta.Kind));
	}

	// Pipeline order of the quartet kinds.
	static readonly BoardKind[] Quartet = [BoardKind.Intake, BoardKind.Ideas, BoardKind.Spec, BoardKind.Work];

	public async Task<MethodologyEnableResult> EnableMethodologyAsync(string projectKey, string preset = "quartet", CancellationToken ct = default)
	{
		// Compat layer (methodology-instance-core): enable still works for $system / legacy
		// callers by ensuring a named instance from the builtin preset, then reporting the
		// boards that instance owns. New code should prefer tasks_methodology_create.
		// The preset selects WHICH board kinds to provision; an unknown slug is rejected
		// before any board is created.
		var provisioning = MethodologyPresets.ResolveProvisioningPreset(preset);
		var instanceName = provisioning.Slug; // "quartet" | "classic"
		if (!await _methodologyInstances.ExistsAsync(projectKey, instanceName, ct))
		{
			// One-act create: rules + boards for the preset kinds.
			var ack = await _methodologyInstances.CreateAsync(projectKey, instanceName, MethodologyInstanceService.SourceBuiltin, provisioning.Slug, ct);
			var runtimeNew = await RuntimeForInstanceAsync(projectKey, instanceName, ct);
			var reportNew = new List<MethodologyEnableBoard>(provisioning.Kinds.Count);
			foreach (var kind in provisioning.Kinds)
			{
				var kindSlug = kind.ToString().ToLowerInvariant();
				var board = ack.Boards.FirstOrDefault(b => string.Equals(b.Kind, kindSlug, StringComparison.OrdinalIgnoreCase));
				var counts = EmptyCounts;
				if (board is not null)
				{
					var view = await GetAsync(projectKey, board.Name, includeClosed: false, ct: ct);
					counts = view.Nodes.GroupBy(n => n.Status, StringComparer.Ordinal)
						.ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
				}
				reportNew.Add(new MethodologyEnableBoard(kindSlug, board?.Name, Created: board is not null, counts, runtimeNew.Blocks(kindSlug)));
			}
			return new MethodologyEnableResult(provisioning.Slug, reportNew);
		}

		// Instance already exists: idempotent re-provision of any missing kinds into it.
		var boards = await _boards.ListAsync(projectKey, ct);
		var createdKinds = new HashSet<BoardKind>();
		foreach (var kind in provisioning.Kinds)
		{
			if (boards.Any(b => b.ClosedAt == null
				&& MethodologyPresets.ParseKind(b.Kind) == kind
				&& string.Equals(b.MethodologyInstance, instanceName, StringComparison.OrdinalIgnoreCase)))
				continue;
			var name = kind.ToString().ToLowerInvariant();
			if (await _boards.ExistsAsync(projectKey, name, ct))
				name = $"{instanceName}-{name}";
			if (await _boards.ExistsAsync(projectKey, name, ct)) continue;
			await CreateBoardAsync(projectKey, name, kind.ToString().ToLowerInvariant(), $"methodology {name}", null, instanceName, ct);
			createdKinds.Add(kind);
		}
		await AutoWireSpecAsync(projectKey, ct);

		var runtime = await RuntimeForInstanceAsync(projectKey, instanceName, ct);
		var after = await _boards.ListAsync(projectKey, ct);
		var report = new List<MethodologyEnableBoard>(provisioning.Kinds.Count);
		foreach (var kind in provisioning.Kinds)
		{
			var kindSlug = kind.ToString().ToLowerInvariant();
			var board = after.FirstOrDefault(b => b.ClosedAt == null
				&& MethodologyPresets.ParseKind(b.Kind) == kind
				&& string.Equals(b.MethodologyInstance, instanceName, StringComparison.OrdinalIgnoreCase));
			var counts = EmptyCounts;
			if (board is not null)
			{
				var view = await GetAsync(projectKey, board.Name, includeClosed: false, ct: ct);
				counts = view.Nodes.GroupBy(n => n.Status, StringComparer.Ordinal)
					.ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
			}
			report.Add(new MethodologyEnableBoard(kindSlug, board?.Name, createdKinds.Contains(kind), counts, runtime.Blocks(kindSlug)));
		}
		return new MethodologyEnableResult(provisioning.Slug, report);
	}

	public Task<string?> ResolveWorkspaceAsync(string projectKey, CancellationToken ct = default) =>
		_boards.FindProjectWorkspaceAsync(projectKey, ct);

	static readonly IReadOnlyDictionary<string, int> EmptyCounts = new Dictionary<string, int>();

	// Surfaced on MethodologyView.Hint when any board's rows were cut by the budget.
	const string TruncationHint =
		"Output budget exceeded: node rows were truncated (see truncated/omitted per board); " +
		"the status histograms (counts) are complete. Narrow the query with includeBoards " +
		"(one board at a time), keep bodyLen:0, or drill into a subtree with tasks_search " +
		"(board + under) for details.";

	// The quartet as one COMPACT INDEX. By default each node is a header row (no body);
	// `bodyLen > 0` slices the first N chars of each body into the row, `includeBoards` (kind
	// names) restricts which quartet boards are returned. `Enabled` reflects true provisioning
	// (all four exist) regardless of the filter. Node rows share one response-wide char
	// budget spent in pipeline order; a board whose rows no longer fit is cut at the first
	// over-budget row and marked Truncated/Omitted — never silently (spec bounded-result-sets).
	public async Task<MethodologyView> GetMethodologyAsync(string projectKey, int bodyLen = 0, string[]? includeBoards = null, string? urlPrefix = null, CancellationToken ct = default)
	{
		var want = ResolveBoardFilter(includeBoards); // null = all quartet boards
		var boards = await _boards.ListAsync(projectKey, ct);
		var result = new List<MethodologyBoard>(Quartet.Length);
		var all = true;
		// One response-wide budget across the four boards, spent in pipeline order; only the
		// node rows are subject to it (the status histograms are always complete).
		var budget = new ResponseBudget();
		var anyTruncated = false;
		foreach (var kind in Quartet)
		{
			var b = boards.FirstOrDefault(x => x.ClosedAt == null && MethodologyPresets.ParseKind(x.Kind) == kind);
			if (b is null) all = false; // existence is a global fact, independent of the filter
			if (want is not null && !want.Contains(kind)) continue;
			var kindName = kind.ToString().ToLowerInvariant();
			if (b is null) { result.Add(new MethodologyBoard(kindName, null, EmptyCounts, [])); continue; }
			var view = await GetAsync(projectKey, b.Name, includeClosed: false, urlPrefix: urlPrefix, ct: ct);
			var counts = view.Nodes
				.GroupBy(n => n.Status, StringComparer.Ordinal)
				.ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
			// Prefix cut: rows keep the board's order (priority then key); the first row that
			// blows the budget stops the board and everything after it counts as omitted.
			var (headers, omitted) = budget.Take(view.Nodes.Select(n => ToHeader(n, bodyLen)).ToList());
			anyTruncated |= omitted > 0;
			result.Add(omitted > 0
				? new MethodologyBoard(kindName, b.Name, counts, headers, Truncated: true, Omitted: omitted)
				: new MethodologyBoard(kindName, b.Name, counts, headers));
		}
		return new MethodologyView(all, result, anyTruncated ? TruncationHint : null);
	}

	// Project the full node view down to an index header, slicing the body to `bodyLen`.
	static PlanNodeHeader ToHeader(PlanNodeView n, int bodyLen) => new(
		n.Key, n.NodeId, n.ParentNodeId, n.ParentSlug, n.Depth,
		n.Status, n.Type, n.Title, n.Priority,
		SliceBody(n.Body, bodyLen), n.Delivery,
		n.Spec, n.BlockedBy, n.LinkedTasks, n.Supersedes, n.Tags, n.Url);

	// bodyLen <= 0 -> no body (pure index). Otherwise the first N chars, with "…" appended
	// when the body was cut. Char-based and predictable (no word-boundary cleverness).
	static string? SliceBody(string? body, int bodyLen)
	{
		if (bodyLen <= 0 || string.IsNullOrEmpty(body)) return null;
		return body.Length <= bodyLen ? body : string.Concat(body.AsSpan(0, bodyLen), "…");
	}

	// Map include_boards (quartet kind names) to a BoardKind set; null/empty = all (no filter). A
	// SOFT filter: an unknown name is silently dropped (not an error) — but if names were PROVIDED
	// and none are quartet boards, the set is EMPTY (→ an empty result at the caller's `!want.Contains`
	// guard), distinct from "none given" (→ null, all boards).
	static HashSet<BoardKind>? ResolveBoardFilter(string[]? includeBoards)
	{
		if (includeBoards is null || includeBoards.Length == 0) return null;
		var set = new HashSet<BoardKind>();
		var anyProvided = false;
		foreach (var raw in includeBoards)
		{
			var name = (raw ?? "").Trim();
			if (name.Length == 0) continue;
			anyProvided = true;
			var match = Quartet.Cast<BoardKind?>().FirstOrDefault(k => k!.Value.ToString().Equals(name, StringComparison.OrdinalIgnoreCase));
			if (match is not null) set.Add(match.Value); // unknown board name → silently dropped (soft filter)
		}
		return anyProvided ? set : null;
	}

	// ---- user-defined methodology definition (storage + live migration via collaborator) ----

	public Task<MethodologyDefAck> DefineMethodologyAsync(
		string projectKey, MethodologyDefinition def, long version,
		IReadOnlyList<MethodologyMigration>? migration = null, CancellationToken ct = default) =>
		_methodologyDefs.DefineAsync(projectKey, def, version, migration, ct);

	public Task<MethodologyDefAck> DeleteMethodologyAsync(string projectKey, long version, CancellationToken ct = default) =>
		_methodologyDefs.DeleteAsync(projectKey, version, ct);

	public Task<MethodologyDefView?> GetMethodologyDefinitionAsync(string projectKey, CancellationToken ct = default) =>
		_methodologyDefs.GetAsync(projectKey, ct);

	// ---- named methodology templates (storage only — never boards / live nodes) ----

	public Task<MethodologyTemplateAck> UpsertMethodologyTemplateAsync(
		string projectKey, string key, MethodologyDefinition def, long version, CancellationToken ct = default) =>
		_methodologyTemplates.UpsertAsync(projectKey, key, def, version, ct);

	public Task<MethodologyTemplateAck> DeleteMethodologyTemplateAsync(
		string projectKey, string key, long version, CancellationToken ct = default) =>
		_methodologyTemplates.DeleteAsync(projectKey, key, version, ct);

	public Task<MethodologyTemplateView?> GetMethodologyTemplateAsync(
		string projectKey, string key, CancellationToken ct = default) =>
		_methodologyTemplates.GetAsync(projectKey, key, ct);

	public Task<IReadOnlyList<MethodologyTemplateListItem>> ListMethodologyTemplatesAsync(
		string projectKey, CancellationToken ct = default) =>
		_methodologyTemplates.ListAsync(projectKey, ct);

	public Task<MethodologyTemplateAck> SnapshotMethodologyTemplateAsync(
		string projectKey, string key, long version, string? from = null, CancellationToken ct = default) =>
		_methodologyTemplates.SnapshotAsync(projectKey, key, version, from, ct);

	// ---- methodology instances (named live process automata) ----

	public Task<MethodologyInstanceAck> CreateMethodologyInstanceAsync(
		string projectKey, string name, string source, string sourceKey, CancellationToken ct = default) =>
		_methodologyInstances.CreateAsync(projectKey, name, source, sourceKey, ct);

	public Task<IReadOnlyList<MethodologyInstanceView>> ListMethodologyInstancesAsync(
		string projectKey, CancellationToken ct = default) =>
		_methodologyInstances.ListAsync(projectKey, ct);

	public Task<MethodologyInstanceView?> GetMethodologyInstanceAsync(
		string projectKey, string name, CancellationToken ct = default) =>
		_methodologyInstances.GetAsync(projectKey, name, ct);

	public Task<MethodologyInstanceAck> CloseMethodologyInstanceAsync(
		string projectKey, string name, CancellationToken ct = default) =>
		_methodologyInstances.CloseAsync(projectKey, name, ct);

	// Product surface over the stored definition — stays on TasksService (guide is derived
	// presentation; definition I/O lives on the collaborator).
	public async Task<MethodologyGuideView> GetMethodologyGuideAsync(string projectKey, CancellationToken ct = default)
	{
		var view = await GetMethodologyDefinitionAsync(projectKey, ct);
		var runtime = view is null ? MethodologyRuntime.PresetsOnly : new MethodologyRuntime(view.Definition);
		// "definition" only when the definition overrides EVERY preset kind (no preset
		// fallback remains); a definition that adds/overrides some kinds is "mixed".
		var source = view is null
			? "presets"
			: Enum.GetValues<BoardKind>().All(k => runtime.IsDefinedKind(k.ToString().ToLowerInvariant())) ? "definition" : "mixed";
		return MethodologyGuide.Render(view?.Definition.Name ?? MethodologyPresets.Name, runtime, source, view?.Version);
	}

	// A specBoard link only makes sense on a work board and must point at an existing spec board.
	async Task ValidateSpecBoardAsync(string projectKey, string kind, string? specBoard, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(specBoard)) return;
		if (MethodologyPresets.ParseKind(kind) != BoardKind.Work)
			throw new ArgumentException($"specBoard applies only to a work board (this board's kind is '{kind}')");
		var target = await _boards.FindAsync(projectKey, specBoard, ct)
			?? throw new ArgumentException($"spec board '{specBoard}' not found in project '{projectKey}'");
		if (MethodologyPresets.ParseKind(target.Kind) != BoardKind.Spec)
			throw new ArgumentException($"'{specBoard}' is not a spec board (kind is '{target.Kind}')");
	}

	async Task EnsureBoard(string projectKey, string board, CancellationToken ct)
	{
		if (!await _boards.ExistsAsync(projectKey, board, ct))
			throw new InvalidOperationException($"task board '{board}' not found in project '{projectKey}'");
	}

	// ---- read: tree view ----

	public async Task<PlanBoardView> GetAsync(string projectKey, string board, bool includeClosed = false, string? under = null, string? urlPrefix = null, string[]? status = null, CancellationToken ct = default)
	{
		await EnsureBoard(projectKey, board, ct);

		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var all = ctx.PlanNodes.Where(n => n.Board == board).ToList();
		var lineage = BuildLineage(all);
		var active = all.Where(n => n.ActiveTo == null).OrderBy(n => n.Priority).ThenBy(n => n.Key).ToList();
		var current = all.Count == 0 ? 0 : all.Max(n => n.Version);

		var meta = (await _boards.FindAsync(projectKey, board, ct))!;
		var runtime = await RuntimeForBoardAsync(projectKey, meta, ct);
		var presetKind = runtime.PresetKind(meta.Kind); // null = definition-resolved kind
		var parentOf = await ParentMapAsync(projectKey, ct);
		// Delivery is gated by kind DATA (MethodologyDeliveryDef), not BoardKind.Spec.
		var deliveryDef = runtime.DeliveryOf(meta.Kind);
		var delivery = deliveryDef is not null
			? await ComputeSpecDeliveryAsync(projectKey, active, parentOf, runtime, deliveryDef, ct)
			: null;

		var index = await BuildNodeIndexAsync(projectKey, ct);
		var tagsByNode = await _tags.BoardTagsAsync(projectKey, board, ct);
		var commitsByNode = await BoardCommitsAsync(ctx, board, ct);
		var underId = ResolveUnderNodeId(under, active);
		// A status filter is an EXPLICIT ask: naming a terminal status returns its nodes even
		// with includeClosed=false (widen the pool first, then keep only the named slugs).
		var statusFilter = TaskSearchFilter.ResolveStatusForKind(status, runtime, meta.Kind);
		var visible = statusFilter is null
			? FilterVisible(active, includeClosed, underId, parentOf, runtime, meta.Kind)
			: FilterVisible(active, includeClosed: true, underId, parentOf, runtime, meta.Kind)
				.Where(n => statusFilter.Contains(n.Status)).ToList();

		var nodes = new List<PlanNodeView>();
		foreach (var n in visible)
		{
			var fromEdges = n.NodeId.Length > 0 ? await _relations.ListAsync(projectKey, n.NodeId, "from", ct: ct) : [];
			var toEdges = n.NodeId.Length > 0 ? await _relations.ListAsync(projectKey, n.NodeId, "to", ct: ct) : [];
			var spec = fromEdges.Where(e => e.Kind == "task_spec").Select(e => LinkRef(e.ToNodeId, index)).ToList();
			var blockedBy = toEdges.Where(e => e.Kind == "blocks").Select(e => LinkRef(e.FromNodeId, index)).ToList();
			var linkedTasks = presetKind == BoardKind.Spec ? toEdges.Where(e => e.Kind == "task_spec").Select(e => LinkRef(e.FromNodeId, index)).ToList() : null;
			var supersedes = fromEdges.Where(e => e.Kind == "supersedes").Select(e => LinkRef(e.ToNodeId, index)).ToList();
			var parentId = parentOf.GetValueOrDefault(n.NodeId);
			nodes.Add(new PlanNodeView(
				Key: n.Key,
				NodeId: n.NodeId,
				ParentNodeId: parentId,
				ParentSlug: parentId is not null && index.TryGetValue(parentId, out var pr) ? pr.Slug : null,
				Depth: DepthOf(n.NodeId, parentOf),
				Status: n.Status,
				Type: n.Type,
				Title: n.Name,
				Body: n.Body,
				Commits: commitsByNode.TryGetValue(n.NodeId, out var cs) ? cs : [],
				Priority: n.Priority,
				Version: n.Version,
				Delivery: delivery is not null && delivery.TryGetValue(n.NodeId, out var dv) ? dv : null,
				Spec: spec.Count > 0 ? spec : null,
				BlockedBy: blockedBy.Count > 0 ? blockedBy : null,
				LinkedTasks: linkedTasks is { Count: > 0 } ? linkedTasks : null,
				Supersedes: supersedes.Count > 0 ? supersedes : null,
				RenamedFrom: lineage.TryGetValue(n.Key, out var p) ? p : [],
				Tags: tagsByNode[n.NodeId].OrderBy(t => t, StringComparer.Ordinal).ToList(),
				// Canonical slug-URL: prefix ends with "/tasks/", append "{board}/{slug}"
				// (node-slug-addressable). board/key are validated slugs → URL-safe.
				Url: urlPrefix is null ? null : urlPrefix + board + "/" + n.Key,
				CreatedAt: n.Created,
				UpdatedAt: n.Updated));
		}
		return new PlanBoardView(current, runtime.KindName(meta.Kind), meta.SpecBoard, nodes);
	}

	public async Task<NodeDetailView?> GetNodeAsync(string projectKey, string nodeId, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(nodeId)) return null;
		var board = await _boards.FindBoardByNodeIdAsync(projectKey, nodeId, ct);
		if (board is null) return null;

		// Reuse GetAsync (includeClosed: the node or its ancestors may be terminal) — it builds
		// the fully-enriched view (links, delivery, parent/depth) we'd otherwise duplicate.
		var view = await GetAsync(projectKey, board, includeClosed: true, ct: ct);
		var node = view.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
		if (node is null) return null;

		// Walk part_of up via ParentNodeId (same-board, single-parent, cycle-guarded) to build
		// the breadcrumb chain, then reverse to root→parent order.
		var byId = view.Nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
		var ancestors = new List<NodeCrumb>();
		var cur = node.ParentNodeId; var guard = 0;
		while (cur is not null && byId.TryGetValue(cur, out var p) && guard++ < 1000)
		{
			ancestors.Add(new NodeCrumb(p.NodeId, p.Key, p.Title));
			cur = p.ParentNodeId;
		}
		ancestors.Reverse();

		// The EXHAUSTIVE relation panel (node-relations-panel): every relation kind in both
		// directions, resolved against the project-wide node index. The board `view` above only
		// carries PlanNodeView's typed subset (spec/blockedBy/…) and no children — this fills the
		// gap so the detail page renders the full graph around a node in one place.
		var relIndex = await BuildNodeIndexAsync(projectKey, ct);
		var fromEdges = await _relations.ListAsync(projectKey, nodeId, "from", ct: ct);
		var toEdges = await _relations.ListAsync(projectKey, nodeId, "to", ct: ct);
		var relations = new List<NodeRelationGroup>();
		foreach (var (kind, fromSide, label) in RelationPanelSpecs)
		{
			var targets = fromSide
				? fromEdges.Where(e => e.Kind == kind).Select(e => e.ToNodeId)
				: toEdges.Where(e => e.Kind == kind).Select(e => e.FromNodeId);
			var links = targets
				.Select(id => LinkRef(id, relIndex))
				.OrderBy(l => l.Board ?? "", StringComparer.Ordinal)
				.ThenBy(l => l.Slug ?? "", StringComparer.Ordinal)
				.ToList();
			if (links.Count > 0) relations.Add(new NodeRelationGroup(label, links));
		}

		return new NodeDetailView(board, view.Kind, node, ancestors, relations);
	}

	// The node detail page's relation panel: every relation kind × direction, in reading order.
	// FromSide=true → this node is the edge's FROM (target = ToNodeId); false → this node is the TO
	// (target = FromNodeId). part_of-forward (the parent) is intentionally absent — the breadcrumb
	// already shows the ancestor chain. Kinds match MethodologyRuntime.ProcessRelationKinds.
	static readonly (string Kind, bool FromSide, string Label)[] RelationPanelSpecs =
	[
		("part_of", false, "children"),
		("blocks", false, "blocked by"),
		("blocks", true, "blocks"),
		("task_spec", true, "implements (spec)"),
		("task_spec", false, "linked tasks"),
		("idea_spec", true, "spec"),
		("idea_spec", false, "idea"),
		("issue_task", true, "tasks"),
		("issue_task", false, "from issue"),
		("supersedes", true, "supersedes"),
		("supersedes", false, "superseded by"),
	];

	public async Task<NodeDetailView?> GetNodeBySlugAsync(string projectKey, string board, string slug, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(board) || string.IsNullOrWhiteSpace(slug)) return null;
		var nodeId = await _boards.FindNodeIdBySlugAsync(projectKey, board, slug, ct);
		if (nodeId is null) return null;
		return await GetNodeAsync(projectKey, nodeId, ct);
	}

	// Addressed single-node read for the MCP surface: `node` is a slug on this board or a
	// 32-hex NodeId (the specRef/partOf convention). Rides the enriched GetNodeAsync path
	// (includeClosed inside), so a TERMINAL node is returned like any other — an addressed
	// ask never gets an empty answer, only the node or a board-naming error.
	public async Task<NodeDetailView> GetNodeOnBoardAsync(string projectKey, string board, string node, string? urlPrefix = null, CancellationToken ct = default)
	{
		await EnsureBoard(projectKey, board, ct);
		var v = (node ?? "").Trim();
		if (v.Length == 0)
			throw new ArgumentException("node is required — a slug key on the board, or a 32-hex NodeId");
		var detail = NodeRefResolver.LooksLikeNodeId(v)
			? await GetNodeAsync(projectKey, v, ct)
			: await GetNodeBySlugAsync(projectKey, board, v.ToLowerInvariant(), ct);
		if (detail is null)
			throw new ArgumentException($"node '{node}' not found on board '{board}' in project '{projectKey}'");
		if (!string.Equals(detail.Board, board, StringComparison.Ordinal))
			throw new ArgumentException($"node '{node}' is on board '{detail.Board}', not '{board}'");
		if (urlPrefix is not null)
			detail = detail with { Node = detail.Node with { Url = urlPrefix + detail.Board + "/" + detail.Node.Key } };
		return detail;
	}

	// Uniform slug-or-NodeId resolution for bare node refs — NodeRefPolicy.Strict.
	public Task<string> ResolveNodeRefAsync(string projectKey, string nodeRef, string? board = null, CancellationToken ct = default) =>
		_nodeRefs.ResolveStrictAsync(projectKey, nodeRef, board, ct);

	// Soft single-node reads — NodeRefPolicy.SoftNull.
	public Task<string?> ResolveNodeRefOrNullAsync(string projectKey, string nodeRef, string? board = null, CancellationToken ct = default) =>
		_nodeRefs.ResolveSoftNullAsync(projectKey, nodeRef, board, ct);

	// Batch `[[slug]]` mention resolution — NodeRefPolicy.BatchRename.
	public Task<IReadOnlyDictionary<string, NodeRefResolution>> ResolveSlugsAsync(string projectKey, IReadOnlyCollection<string> slugs, CancellationToken ct = default) =>
		_nodeRefs.ResolveBatchRenameAsync(projectKey, slugs, ct);

	// exact-identifier-search-surfacing (spec) — NodeRefPolicy.MultiHit: soft multi-hit ids from
	// the resolver, then GetNodeAsync enrichment (includeClosed) so terminal nodes surface.
	// Empty list on a miss; `board` narrows; multi-board slug returns ALL matches ordered by board.
	public async Task<IReadOnlyList<TaskSearchHit>> ExactIdentifierHitsAsync(string projectKey, string identifier, string? board = null, string? urlPrefix = null, CancellationToken ct = default)
	{
		TaskSearchHit? Enrich(NodeDetailView? detail)
		{
			if (detail is null) return null;
			if (board is not null && !string.Equals(detail.Board, board, StringComparison.Ordinal)) return null;
			var node = urlPrefix is null ? detail.Node : detail.Node with { Url = urlPrefix + detail.Board + "/" + detail.Node.Key };
			return new TaskSearchHit(detail.Board, node);
		}

		var hits = new List<TaskSearchHit>();
		foreach (var id in _nodeRefs.ResolveMultiHitIds(projectKey, identifier, board))
			if (Enrich(await GetNodeAsync(projectKey, id, ct)) is { } hit) hits.Add(hit);
		// Deterministic order between exact matches (spec: by board) so a multi-board slug is stable.
		// NodeId path is ≤1 hit — OrderBy is a no-op there (was an early return before).
		return hits.OrderBy(h => h.Board, StringComparer.Ordinal).ToList();
	}

	// Instance-scoped relation-kind vocabulary check (primitives-link-kinds +
	// methodology-instance-scoped-axes): builtin process + neutral kinds plus the
	// linkKinds declared on the FROM node's board instance. The store no longer validates
	// the vocabulary itself — this is the one door for user-supplied kinds.
	// Transitional: no fromNodeId / no board membership → project-singleton definition.
	public async Task<string> ValidateRelationKindAsync(string projectKey, string kind, string? fromNodeId = null, CancellationToken ct = default)
	{
		var k = (kind ?? "").Trim().ToLowerInvariant();
		var (runtime, scopeLabel) = await RuntimeForRelationAsync(projectKey, fromNodeId, ct);
		if (!runtime.IsValidRelationKind(k))
			throw new ArgumentException(
				$"invalid relation kind '{kind}'; valid for {scopeLabel}: {string.Join("|", runtime.KnownRelationKinds())}" +
				" (declare additional kinds on the methodology instance's linkKinds)");
		return k;
	}

	// Resolve the runtime that owns a relation's declared-kind vocabulary: the FROM
	// node's board → its methodology instance when membership is set; else the project's
	// legacy singleton definition (boards without MethodologyInstance still exist during
	// the dual-read transition).
	async Task<(MethodologyRuntime Runtime, string ScopeLabel)> RuntimeForRelationAsync(
		string projectKey, string? fromNodeId, CancellationToken ct)
	{
		if (!string.IsNullOrWhiteSpace(fromNodeId))
		{
			var boardName = await _boards.FindBoardByNodeIdAsync(projectKey, fromNodeId, ct);
			if (boardName is not null && await _boards.FindAsync(projectKey, boardName, ct) is { } meta)
			{
				var runtime = await RuntimeForBoardAsync(projectKey, meta, ct);
				var label = !string.IsNullOrWhiteSpace(meta.MethodologyInstance)
					? $"methodology instance '{meta.MethodologyInstance}'"
					: "this project";
				return (runtime, label);
			}
		}
		return (await RuntimeAsync(projectKey, ct), "this project");
	}

	// nodeId -> its active part_of parent nodeId (single parent). One query, project-wide.
	async Task<Dictionary<string, string>> ParentMapAsync(string projectKey, CancellationToken ct) =>
		(await _relations.ListByKindAsync(projectKey, "part_of", ct))
			.GroupBy(e => e.FromNodeId, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.First().ToNodeId, StringComparer.Ordinal);

	// Distance from a root along part_of (0 = root). Guarded against cycles.
	static int DepthOf(string nodeId, Dictionary<string, string> parentOf)
	{
		var d = 0; var cur = nodeId; var guard = 0;
		while (parentOf.TryGetValue(cur, out var par) && guard++ < 1000) { d++; cur = par; }
		return d;
	}

	// Resolve `under` (a flat slug on this board) to its nodeId, or null if absent/unset.
	static string? ResolveUnderNodeId(string? under, List<PlanNode> active)
	{
		if (string.IsNullOrWhiteSpace(under)) return null;
		var slug = TaskSlug.Validate(under);
		return active.FirstOrDefault(n => n.Key == slug)?.NodeId;
	}

	// Tag-projection: bucket the board's active nodes by their tag value in each `groupBy`
	// namespace, nested in dimension order (e.g. [area, concern] → area buckets, each split
	// by concern). A node with several values in a dimension lands in several buckets;
	// untagged nodes go to "(none)" (ordered last). Every group — at every level — carries a
	// delivery roll-up over its nodes (spec boards). This is a VIEW: part_of is never touched
	// (tag-grouping-is-projection).
	public async Task<GroupedBoardView> GetGroupedAsync(string projectKey, string board, IReadOnlyList<string> groupBy, CancellationToken ct = default)
	{
		await EnsureBoard(projectKey, board, ct);
		var meta = (await _boards.FindAsync(projectKey, board, ct))!;
		// Board → instance membership scopes tagAxes (methodology-instance-scoped-axes);
		// unassigned boards fall back to the project-singleton definition.
		var runtime = await RuntimeForBoardAsync(projectKey, meta, ct);

		// Grouping dimensions validate against the board kind's tag axes; an axis-less
		// (free-form) kind keeps the builtin preset pair as its grouping namespaces —
		// free-form nodes may carry any namespace, and area/concern grouping has always
		// worked on simple boards.
		var axes = runtime.TagAxes(meta.Kind);
		var allowed = (axes.Count > 0 ? axes : MethodologyPresets.BuiltinAxes).Select(a => a.Namespace).ToList();
		var dims = (groupBy ?? []).Select(d => (d ?? "").Trim().ToLowerInvariant()).Where(d => d.Length > 0).ToList();
		if (dims.Count == 0)
			throw new ArgumentException($"groupBy needs at least one tag namespace ({string.Join("|", allowed)})");
		foreach (var ns in dims)
			if (!allowed.Contains(ns))
				throw new ArgumentException($"groupBy must be tag namespaces ({string.Join("|", allowed)}); got '{ns}'");

		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var active = ctx.PlanNodes.Where(n => n.Board == board && n.ActiveTo == null).ToList();
		var tagsByNode = await _tags.BoardTagsAsync(projectKey, board, ct);
		var deliveryDef = runtime.DeliveryOf(meta.Kind);
		var delivery = deliveryDef is not null
			? await ComputeSpecDeliveryAsync(projectKey, active, await ParentMapAsync(projectKey, ct), runtime, deliveryDef, ct)
			: null;

		var groups = ProjectByTags(active, dims, 0, tagsByNode, delivery);
		return new GroupedBoardView(dims, runtime.KindName(meta.Kind), groups);
	}

	// Recursively bucket `nodes` by dimension `dims[depth]`, then by the remaining dimensions.
	// The final dimension yields leaf groups (NodeKeys filled, SubGroups empty); earlier ones
	// yield nesting groups (SubGroups filled, NodeKeys empty). Each group's delivery rolls up
	// over all its nodes regardless of depth.
	static List<TagGroup> ProjectByTags(
		List<PlanNode> nodes, IReadOnlyList<string> dims, int depth,
		ILookup<string, string> tagsByNode, IReadOnlyDictionary<string, string>? delivery)
	{
		var prefix = dims[depth] + ":";
		var buckets = new Dictionary<string, List<PlanNode>>(StringComparer.Ordinal);
		foreach (var n in nodes)
		{
			var vals = tagsByNode[n.NodeId].Where(t => t.StartsWith(prefix, StringComparison.Ordinal)).ToList();
			foreach (var key in vals.Count > 0 ? vals : ["(none)"])
				(buckets.TryGetValue(key, out var l) ? l : buckets[key] = []).Add(n);
		}

		var last = depth == dims.Count - 1;
		return buckets
			.OrderBy(b => b.Key == "(none)" ? 1 : 0) // "(none)" last
			.ThenBy(b => b.Key, StringComparer.Ordinal)
			.Select(b => new TagGroup(
				b.Key,
				delivery is null ? null : CombineDelivery(b.Value.Select(n => delivery.GetValueOrDefault(n.NodeId))),
				last ? b.Value.OrderBy(n => n.Priority).ThenBy(n => n.Key, StringComparer.Ordinal).Select(n => n.Key).ToList() : [],
				last ? [] : ProjectByTags(b.Value, dims, depth + 1, tagsByNode, delivery)))
			.ToList();
	}

	// Roll up a group's per-node delivery into one status. not_started if all are (or none);
	// done only if all done; done_with_defects if all terminal with a defect; else in_progress.
	static string? CombineDelivery(IEnumerable<string?> deliveries)
	{
		var ds = deliveries.Where(d => d is not null).Select(d => d!).ToList();
		if (ds.Count == 0 || ds.All(d => d == "not_started")) return "not_started";
		if (ds.All(d => d == "done")) return "done";
		if (ds.All(d => d is "done" or "done_with_defects")) return "done_with_defects";
		return "in_progress";
	}

	public Task<IReadOnlyList<PlanNode>> ListActiveNodesAsync(string projectKey, string board, CancellationToken ct = default)
	{
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		IReadOnlyList<PlanNode> active = ctx.PlanNodes.Where(n => n.Board == board && n.ActiveTo == null).ToList();
		return Task.FromResult(active);
	}

	// Hide terminal (closed) nodes unless includeClosed; keep terminal part_of ancestors of
	// any visible node so the tree stays connected. `underId` restricts to a part_of subtree.
	// Terminality is per the board's kind (a definition kind classifies by its own vocab).
	static List<PlanNode> FilterVisible(List<PlanNode> active, bool includeClosed, string? underId, Dictionary<string, string> parentOf, MethodologyRuntime runtime, string? kindSlug)
	{
		IEnumerable<PlanNode> scoped = active;
		if (underId is not null)
			scoped = active.Where(n => TaskSearchFilter.InSubtree(n.NodeId, underId, parentOf));
		var pool = scoped.ToList();
		if (includeClosed) return pool;

		var keep = new HashSet<string>(StringComparer.Ordinal); // nodeIds
		foreach (var n in pool.Where(n => !runtime.IsTerminalStatus(kindSlug, n.Status)))
		{
			keep.Add(n.NodeId);
			var cur = n.NodeId; var guard = 0;
			while (parentOf.TryGetValue(cur, out var par) && guard++ < 1000) { keep.Add(par); cur = par; }
		}
		return pool.Where(n => keep.Contains(n.NodeId)).ToList();
	}

	// A resolvable reference to a node anywhere in the project (links cross boards).
	sealed record NodeRef(string Board, string BoardKind, string Slug, string Title, string Status, string Type);

	// nodeId -> NodeRef across every board in the project (links bind to nodeId, which is
	// globally unique, so a link target may live on another board).
	async Task<Dictionary<string, NodeRef>> BuildNodeIndexAsync(string projectKey, CancellationToken ct)
	{
		var index = new Dictionary<string, NodeRef>(StringComparer.Ordinal);
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		foreach (var b in await _boards.ListAsync(projectKey, ct))
			foreach (var n in ctx.PlanNodes.Where(x => x.Board == b.Name && x.ActiveTo == null).ToList())
				if (n.NodeId.Length > 0)
					index[n.NodeId] = new NodeRef(b.Name, b.Kind, n.Key, n.Name, n.Status, n.Type);
		return index;
	}

	static LinkDto LinkRef(string nodeId, Dictionary<string, NodeRef> index) =>
		index.TryGetValue(nodeId, out var r)
			? new LinkDto(nodeId, r.Board, r.Slug, r.Title, r.Status)
			: new LinkDto(nodeId, null, null, null, "missing");

	// COMPUTED delivery roll-up (keyed by NodeId): a node's delivery derives bottom-up from
	// the tasks linked (task_spec) to it and its part_of descendants (decomposition may cross
	// boards) — each leaf resolves independently before combining, rather than pooling every
	// task in the subtree into one flat union. Type roles come from `def` (methodology data),
	// not string literals (primitives-enum-residual).
	async Task<Dictionary<string, string>> ComputeSpecDeliveryAsync(
		string projectKey, IReadOnlyList<PlanNode> specNodes, Dictionary<string, string> parentOf,
		MethodologyRuntime runtime, MethodologyDeliveryDef def, CancellationToken ct)
	{
		var byNodeId = new Dictionary<string, (string Type, string Status)>(StringComparer.Ordinal);
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		foreach (var b in await _boards.ListAsync(projectKey, ct))
			foreach (var n in ctx.PlanNodes.Where(x => x.Board == b.Name && x.ActiveTo == null).ToList())
				if (n.NodeId.Length > 0) byNodeId[n.NodeId] = (n.Type, n.Status);

		// childrenOf (invert part_of) and tasksOf (inbound task_spec), each one query.
		var childrenOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var (child, parent) in parentOf)
			(childrenOf.TryGetValue(parent, out var l) ? l : childrenOf[parent] = []).Add(child);
		var tasksOf = (await _relations.ListByKindAsync(projectKey, "task_spec", ct))
			.GroupBy(e => e.ToNodeId, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.Select(e => e.FromNodeId).ToList(), StringComparer.Ordinal);

		// Bottom-up, memoized: a node's delivery combines its OWN directly-linked tasks (if any)
		// with each child's recursively-computed delivery, via the same CombineDelivery used for
		// tag-group rollups. A leaf with no linked tasks has no inputs at all, so CombineDelivery
		// defaults it to not_started — it no longer disappears into a flat subtree-wide task union
		// (which used to hide task-less leaves and let an umbrella wrongly read as `done` when some
		// leaves had no work whatsoever).
		var memo = new Dictionary<string, string>(StringComparer.Ordinal);
		var visiting = new HashSet<string>(StringComparer.Ordinal);
		string DeliveryOf(string nodeId)
		{
			if (memo.TryGetValue(nodeId, out var cached)) return cached;
			if (!visiting.Add(nodeId)) return "not_started"; // cycle guard; part_of shouldn't cycle
			string? own = tasksOf.TryGetValue(nodeId, out var ts)
				? Delivery(ts.Where(byNodeId.ContainsKey).Select(id => byNodeId[id]).ToList(), runtime, def)
				: null;
			var childDeliveries = childrenOf.TryGetValue(nodeId, out var kids)
				? kids.Select(DeliveryOf)
				: Enumerable.Empty<string>();
			var result = CombineDelivery(own is null ? childDeliveries : childDeliveries.Append(own)) ?? "not_started";
			visiting.Remove(nodeId);
			memo[nodeId] = result;
			return result;
		}

		return specNodes.ToDictionary(s => s.NodeId, s => DeliveryOf(s.NodeId), StringComparer.Ordinal);
	}

	// Type roles from methodology data: RequiredTypes drive progress; DefectTypes open while
	// requireds are done yield done_with_defects. No hardcoded "feature"/"bug" here.
	static string Delivery(List<(string Type, string Status)> tasks, MethodologyRuntime runtime, MethodologyDeliveryDef def)
	{
		var required = tasks.Where(t => def.RequiredTypes.Contains(t.Type, StringComparer.OrdinalIgnoreCase)).ToList();
		if (required.Count == 0) return "not_started";
		if (!required.All(f => runtime.KindOfSlug(f.Status) == StatusKind.TerminalOk)) return "in_progress";
		var openDefect = tasks.Any(t =>
			def.DefectTypes.Contains(t.Type, StringComparer.OrdinalIgnoreCase)
			&& runtime.KindOfSlug(t.Status) == StatusKind.Open);
		return openDefect ? "done_with_defects" : "done";
	}

	// ---- write: upsert ----

	public async Task<UpsertOutcome> UpsertAsync(string projectKey, string board, IReadOnlyList<NodePatch> nodes, TasksActor? actor = null, CancellationToken ct = default)
	{
		actor ??= TasksActor.None;
		using var op = PetBoxActivitySources.Tasks.StartActivity("tasks.upsert");
		op?.SetTag("petbox.project", projectKey);
		op?.SetTag("petbox.board", board);
		op?.SetTag("petbox.node_count", nodes.Count);

		await _boards.EnsureAsync(projectKey, board, ct); // auto-vivify on first write
		var meta = (await _boards.FindAsync(projectKey, board, ct))!;
		if (meta.ClosedAt != null)
			throw new InvalidOperationException($"board '{board}' is closed — reopen it (tasks_board_reopen) before writing");

		var runtime = await RuntimeForBoardAsync(projectKey, meta, ct);
		var kindSlug = meta.Kind;
		var presetKind = runtime.PresetKind(kindSlug); // null = definition-resolved kind
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var prior = ctx.PlanNodes.Where(n => n.Board == board && n.ActiveTo == null).ToList()
			.ToDictionary(n => n.Key, n => n, StringComparer.Ordinal);

		// Split the batch: a deleted patch carries only Key + Version (a temporal-close, no new
		// revision), so everything downstream — workflow, guards, links, tags, partOf, supersedes —
		// is built from the upsert patches only. That also means a spec-node delete needs no
		// ideaRef: erasing junk is not a spec change (retiring a real requirement is `deprecated`).
		var deletePatches = nodes.Where(p => p.Deleted).DistinctBy(p => p.Key, StringComparer.Ordinal).ToList();
		var upsertPatches = nodes.Where(p => !p.Deleted).ToList();
		if (deletePatches.Count > 0)
		{
			if (deletePatches.Any(p => p.PrevKey is not null))
				throw new ArgumentException("a node cannot be renamed and deleted in the same patch");
			var both = deletePatches.Select(p => p.Key)
				.Intersect(upsertPatches.Select(p => p.Key), StringComparer.Ordinal).FirstOrDefault();
			if (both is not null)
				throw new ArgumentException($"node '{both}' is both deleted and upserted in one batch — pick one");
		}

		var desired = upsertPatches.Select(p => ApplyWorkflow(runtime, kindSlug, Merge(p, prior), prior, actor, p.Reason) with { Board = board }).ToArray();
		ValidateChanges(desired, prior);
		// Resolve slug specRefs to NodeIds ONCE, up front — both the validation below and the
		// task_spec edge write (LinkRefsAsync) read this map, so it must never carry a raw slug.
		var specRefs = ResolveSpecRefs(projectKey, meta, LinkFields(upsertPatches, p => p.SpecRef));
		// blockedBy resolves ONCE up front too, so the `blocks` edge always carries a NodeId.
		var blockedBy = ResolveBlockedBy(board, desired, prior, LinkFields(upsertPatches, p => p.BlockedBy));
		var ideaRefs = LinkFields(upsertPatches, p => p.IdeaRef);
		using (PetBoxActivitySources.Tasks.StartActivity("tasks.upsert.guards"))
		{
			RequireDefinitionLinks(runtime, kindSlug, desired, prior, specRefs, blockedBy, ideaRefs);
			await ValidateLinkTargetsAsync(projectKey, meta, kindSlug, runtime, specRefs, ideaRefs, ct);
			await RequireBlockersAsync(presetKind, projectKey, desired, blockedBy, ct);
			await RequirePreconditionArtifactsAsync(runtime, kindSlug, projectKey, board, desired, prior, ct);
		}

		// Children guard: a node with active part_of children is not deletable — unless its
		// children die in the SAME batch (so a subtree can go in one call, bottom-up implied).
		// Refusals ride the conflict shape (applied:false, nothing written), not an exception,
		// so the caller gets the per-key reason plus the fresh delta to rebase on.
		var dels = new List<(string Key, long Version)>();
		if (deletePatches.Count > 0)
		{
			var guardConflicts = new List<TemporalConflict>();
			var dyingIds = deletePatches
				.Select(p => prior.GetValueOrDefault(p.Key)?.NodeId)
				.Where(id => !string.IsNullOrEmpty(id)).Cast<string>()
				.ToHashSet(StringComparer.Ordinal);
			foreach (var p in deletePatches)
			{
				if (!prior.TryGetValue(p.Key, out var row))
					continue; // idempotent: nothing active to delete
				if ((await ActivePartOfChildrenAsync(projectKey, row.NodeId, ct)).Any(c => !dyingIds.Contains(c)))
					guardConflicts.Add(new(p.Key, TemporalConflictKind.Rejected, p.Version, row.Version,
						"node has active part_of children — delete them first (or in the same batch)"));
				else
					dels.Add((p.Key, p.Version));
			}
			if (guardConflicts.Count > 0)
			{
				// Not applied; the ack still carries the fresh cursor + the caller's own rows.
				var delta = await TemporalStore.UpsertAsync(ctx, Array.Empty<PlanNode>(), 0,
					partition: n => n.Board == board, ct: ct);
				delta = delta with { Applied = false, Conflicts = guardConflicts };
				return new UpsertOutcome(ScopeEchoToCall(delta, nodes, delta.CurrentVersion), runtime.KindName(kindSlug));
			}
		}
		// Class-A lexical floor written INSIDE the entity tx: open nodes (re)indexed, terminal/
		// removed nodes dropped (search covers only the open set), committing/rolling back with the
		// entity. Tags read in-tx are the pre-upsert set; SetTagsAsync (below) is reflected by the
		// post-commit RefreshFtsTagsAsync. Vectors are materialized by the worker, not here.
		var fts = new SqliteFtsIndex(() => ctx);
		TemporalUpsertResult<PlanNode> r;
		using (var temporalSpan = PetBoxActivitySources.Tasks.StartActivity("tasks.upsert.temporal"))
		{
			r = await TemporalStore.UpsertAsync(ctx, desired, dels, 0,
				onWithinTx: async (tx, upserted, deletedKeys, c) =>
				{
					var tags = await NodeTagsAsync(tx, board, upserted.Where(n => TasksSearchDocs.IsIndexable(n, runtime)).Select(n => n.NodeId), c);
					foreach (var n in upserted)
						if (TasksSearchDocs.IsIndexable(n, runtime))
							await fts.IndexAsync(tx, TasksSearchDocs.ToDoc(n, projectKey, tags.GetValueOrDefault(n.NodeId, [])), c);
						else
							await fts.DeleteAsync(tx, projectKey, board, n.Key, c); // left the open set
					foreach (var key in deletedKeys)
						await fts.DeleteAsync(tx, projectKey, board, key, c);
				},
				partition: n => n.Board == board, ct: ct);
			// Concurrency outcomes as COUNTS/kinds only (privacy contract: forms and sizes,
			// never values) — before this, a Stale outcome was invisible in telemetry
			// (intake stale-baseline-blind-retry).
			if (r.Conflicts.Count > 0)
			{
				temporalSpan?.SetTag("petbox.conflicts", r.Conflicts.Count);
				temporalSpan?.SetTag("petbox.conflict_kinds", string.Join(",", r.Conflicts.Select(c => c.Kind.ToString()).Distinct()));
			}
			if (r.AutoResolved.Count > 0)
				temporalSpan?.SetTag("petbox.auto_resolved", r.AutoResolved.Count);
		}
		// The main write's cursor: any row revision beyond it was written by THIS call's
		// cascade effects below (supersedes obsoletion, unblocking) — the echo scoping keys on it.
		var mainCursor = r.CurrentVersion;
		if (r.Applied)
			using (PetBoxActivitySources.Tasks.StartActivity("tasks.upsert.links"))
			{
				await _boards.TouchAsync(projectKey, board, ct);
				await LinkRefsAsync(projectKey, "task_spec", desired, specRefs, blockerIsFrom: false, ct);
				await LinkRefsAsync(projectKey, "blocks", desired, blockedBy, blockerIsFrom: true, ct);
				await LinkRefsAsync(projectKey, "idea_spec", desired, ideaRefs, blockerIsFrom: true, ct);
				await CloseBlocksOnLeaveAsync(projectKey, desired, prior, ct);
				await _effects.RunTransitionEffectsAsync(projectKey, kindSlug, runtime, desired, prior, ct);
				await _effects.RunDeleteEffectsAsync(projectKey, board, deletePatches, prior, runtime, ct);
			}
		// Tags + part_of are node metadata, not a content revision — apply whenever the
		// upsert did not conflict (so a pure tag/parent change on an unchanged node still
		// takes effect; on a no-op the NodeId in `desired` is the existing one).
		if (r.Conflicts.Count == 0)
			using (PetBoxActivitySources.Tasks.StartActivity("tasks.upsert.meta"))
			{
				await _associations.SetTagsAsync(projectKey, board, runtime, kindSlug, upsertPatches, desired, ct);
				await TaskUpsertAssociations.SetCommitsAsync(ctx, board, upsertPatches, desired, ct);
				await _associations.SetPartOfAsync(projectKey, board, upsertPatches, desired, ct);
				await _associations.SetSupersedesAsync(projectKey, board, upsertPatches, desired, runtime, ct);
				// RequiresReason reasons land as comments after the node write (same post-tx
				// style as tags/partOf — not inside the temporal node transaction).
				if (r.Applied)
					await PersistReasonCommentsAsync(projectKey, board, runtime, kindSlug, upsertPatches, desired, prior, ct);
			}
		// Refresh the FTS Tags column now that SetTagsAsync (above) has run: the in-tx index wrote
		// content + pre-upsert tags transactionally; re-index this batch's open nodes with the
		// now-current tags. Content/membership are already committed with the entity; vectors are
		// materialized off the write path by the async-vectorization worker.
		if (r.Applied)
			using (PetBoxActivitySources.Tasks.StartActivity("tasks.upsert.fts-tags"))
				await RefreshFtsTagsAsync(ctx, projectKey, board, desired, runtime, ct);
		if (r.Applied)
		{
			// Post-effects re-read: cascade revisions (a superseded node moved to terminal-cancel,
			// a Blocked task unblocked) land AFTER the main temporal write, so refresh the delta
			// and cursor once the effects have run — the echo then reflects what this call actually
			// did, and CurrentVersion is a cursor an immediate DeltaAsync returns nothing for.
			var (added, updated, removed, current) = await TemporalStore.ChangesSinceAsync<PlanNode>(
				ctx, 0, partition: n => n.Board == board, ct: ct);
			r = r with
			{
				CurrentVersion = current,
				Added = await AttachCommitsAsync(ctx, board, added, ct),
				Updated = await AttachCommitsAsync(ctx, board, updated, ct),
				Removed = removed,
			};
		}
		return new UpsertOutcome(ScopeEchoToCall(r, nodes, mainCursor), runtime.KindName(kindSlug));
	}

	// Scope a write echo to THIS call (spec sinceversion-contract — the write-ack carries no
	// delta): keep only rows whose key the call mentioned (patch keys + rename sources) plus
	// rows revised by the call's own cascade effects (Version > the main write's cursor —
	// e.g. a `supersedes` target obsoleted, an unblocked task). Other writers' history is
	// never echoed — a full board delta is DeltaAsync's job; CurrentVersion (the cursor) is
	// untouched.
	static TemporalUpsertResult<PlanNode> ScopeEchoToCall(TemporalUpsertResult<PlanNode> r, IReadOnlyList<NodePatch> patches, long mainCursor)
	{
		// A write that did NOT apply changed nothing — the echo must be empty so the ack reads
		// unambiguously as "not applied" (spec upsert-ack-echo-clean). The whole story is in
		// Conflicts (each with its baseline/active version); a caller rebases via tasks_delta.
		// Without this, Added/Updated carry the mentioned nodes' CURRENT state and read as if
		// the write landed.
		if (!r.Applied)
			return r with { Added = [], Updated = [], Removed = [] };

		var mentioned = patches.Select(p => p.Key)
			.Concat(patches.Where(p => p.PrevKey is not null).Select(p => p.PrevKey!))
			.ToHashSet(StringComparer.Ordinal);
		bool Own(PlanNode n) => mentioned.Contains(n.Key) || n.Version > mainCursor;
		return r with
		{
			Added = r.Added.Where(Own).ToList(),
			Updated = r.Updated.Where(Own).ToList(),
			Removed = r.Removed.Where(k => mentioned.Contains(k)).ToList(),
		};
	}

	public async Task<UpsertOutcome> DeltaAsync(string projectKey, string board, long sinceVersion, CancellationToken ct = default)
	{
		await EnsureBoard(projectKey, board, ct);
		var meta = (await _boards.FindAsync(projectKey, board, ct))!;
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var r = await TemporalStore.UpsertAsync(ctx, Array.Empty<PlanNode>(), sinceVersion, partition: n => n.Board == board, ct: ct);
		r = r with
		{
			Added = await AttachCommitsAsync(ctx, board, r.Added, ct),
			Updated = await AttachCommitsAsync(ctx, board, r.Updated, ct),
		};
		return new UpsertOutcome(r, (await RuntimeAsync(projectKey, ct)).KindName(meta.Kind));
	}

	// ---- read: unified search (list = search without a query; uniform-entity-verbs v2) ----

	// The generic uniform-read seam (implemented EXPLICITLY — the contract is a shared shape,
	// not a DI dispatch point): the plain envelope of the rich SearchNodesAsync. Budget
	// markers stay null here — the response budget is measured on the WIRE rows, so it
	// belongs to the adapter that shapes them.
	async Task<SearchEnvelope<TaskSearchHit>> ISearchService<TaskSearchHit, TaskNodeFilter, TaskSortBy>.SearchAsync(
		string projectKey, SearchRequest<TaskNodeFilter, TaskSortBy> request, CancellationToken ct)
	{
		var r = await SearchNodesAsync(projectKey, request, urlPrefix: null, ct: ct);
		return new SearchEnvelope<TaskSearchHit>(r.Hits, Retrievers: r.Retrievers);
	}

	public async Task<TaskSearchResult> SearchNodesAsync(string projectKey, SearchRequest<TaskNodeFilter, TaskSortBy> request, string? urlPrefix = null, CancellationToken ct = default)
	{
		var req = request;
		var f = req.Filter ?? new TaskNodeFilter();
		var query = string.IsNullOrWhiteSpace(req.Query) ? null : req.Query.Trim();
		if (query is null && req.Sort is { By: TaskSortBy.Relevance })
			throw new ArgumentException("sort by relevance needs a query (q) — without one the read is a deterministic listing (default order: priority then key)");

		var boardFilter = string.IsNullOrWhiteSpace(f.Board) ? null : f.Board;
		if (boardFilter is not null) await EnsureBoard(projectKey, boardFilter, ct);

		// The boards in scope (one, or the whole project) — kinds drive status validation,
		// metas carry the board context of a board-scoped read.
		var boardsMeta = await _boards.ListAsync(projectKey, ct);
		if (boardFilter is not null)
			boardsMeta = boardsMeta.Where(b => string.Equals(b.Name, boardFilter, StringComparison.Ordinal)).ToList();

		// Soft identifier filters (keys/under): miss-tolerant multi-hit NodeIds via the NodeRef
		// MultiHit policy — NOT strict addressing (that stays on ResolveNodeRefAsync / tasks_node_get).
		// A key that matches nothing contributes nothing; an ambiguous slug contributes ALL matches.
		// under-not-given → null (no filter); under-given-but-missed → empty roots → no node passes.
		HashSet<string>? keyIds = null;
		if (f.Keys is { Count: > 0 })
		{
			keyIds = new HashSet<string>(StringComparer.Ordinal);
			foreach (var k in f.Keys)
				foreach (var id in _nodeRefs.ResolveMultiHitIds(projectKey, k, boardFilter))
					keyIds.Add(id);
		}
		HashSet<string>? underRoots = null;
		if (!string.IsNullOrWhiteSpace(f.Under))
		{
			underRoots = new HashSet<string>(StringComparer.Ordinal);
			foreach (var id in _nodeRefs.ResolveMultiHitIds(projectKey, f.Under, boardFilter))
				underRoots.Add(id);
		}
		var parentOf = underRoots is null ? null : await ParentMapAsync(projectKey, ct);
		var runtime = await RuntimeAsync(projectKey, ct);
		var statusFilter = TaskSearchFilter.ResolveStatusAcross(f.Status, runtime, boardsMeta.Select(b => b.Kind));

		// Reverse commit lookup (node-commits-impl): NodeIds carrying the commit, resolved once
		// into criteria so the post-select applicator stays pure.
		HashSet<string>? commitNodeIds = null;
		if (!string.IsNullOrWhiteSpace(f.Commit))
		{
			using var commitCtx = _boards.NewEnsuredConnection(projectKey);
			commitNodeIds = await NodesCarryingCommitAsync(commitCtx, f.Commit, ct);
		}

		var criteria = new TaskSearchCriteria(
			UnderRoots: underRoots,
			ParentOf: parentOf,
			StatusSlugs: statusFilter,
			KeyNodeIds: keyIds,
			CommitNodeIds: commitNodeIds);

		List<TaskSearchHit> hits;
		SearchRetrievers? retrievers = null;
		long? currentVersion = null;
		if (query is null)
		{
			// LISTING: full PlanNodeView enrichment per board (links/delivery/parent/commits).
			// MCP listing wire keeps the full row; lean projection would strip fields callers need.
			// A status filter or explicit keys are an EXPLICIT ask — widen the pool to terminal
			// nodes first (mirrors GetAsync's own status handling), then criteria keep only what
			// was asked for.
			var widen = f.IncludeClosed || statusFilter is not null || keyIds is not null;
			hits = new List<TaskSearchHit>();
			foreach (var b in boardsMeta)
			{
				var view = await GetAsync(projectKey, b.Name, includeClosed: widen, urlPrefix: urlPrefix, ct: ct);
				if (boardFilter is not null) currentVersion = view.CurrentVersion;
				hits.AddRange(view.Nodes.Select(n => new TaskSearchHit(b.Name, n)));
			}
		}
		else
		{
			// QUERY: hybrid selection over the OPEN set (terminal nodes are not indexed).
			// Candidates are LEAN-projected (no per-node relation panel) — enough for sort + the
			// MCP lean wire cut. The fused ranking supplies a bounded CANDIDATE POOL of
			// max(3×limit, 50) — 3× leaves post-fusion predicates room to drop candidates and
			// still fill `limit`; the 50 floor keeps recall sane for small limits.
			(hits, retrievers) = await HybridCandidatesAsync(projectKey, query, boardFilter,
				Math.Max(req.Limit * 3, 50), urlPrefix, runtime, ct);

			// exact-identifier-search-surfacing (spec): a query that exactly matches a node's
			// slug reads as an addressed ask in disguise — surface EVERY exact match even when
			// terminal (closed nodes aren't indexed for relevance), ahead of the fused ranking.
			// Still subject to the criteria predicates below like any other hit.
			var exactHits = await ExactIdentifierHitsAsync(projectKey, query, boardFilter, urlPrefix, ct);
			// An addressed match has no fused score (Score stays null); it is confirmed by identity.
			var freshExact = exactHits.Where(e => !hits.Any(h => h.Board == e.Board && h.Node.Key == e.Node.Key))
				.Select(e => e with { Retriever = "exact" }).ToList();
			hits.InsertRange(0, freshExact);
		}

		hits = TaskSearchFilter.Apply(hits, criteria);

		hits = SortHits(projectKey, hits, req.Sort, hasQuery: query is not null);
		if (req.Limit > 0 && hits.Count > req.Limit) hits = hits.Take(req.Limit).ToList();
		if (req.BodyLen > 0)
			hits = hits.Select(h => h with { Node = h.Node with { Body = SnippetBody(h.Node.Body, req.BodyLen) } }).ToList();

		var meta = boardFilter is null || boardsMeta.Count == 0 ? null : boardsMeta[0];
		return new TaskSearchResult(
			hits,
			Board: meta?.Name,
			Kind: meta is null ? null : runtime.KindName(meta.Kind),
			SpecBoard: meta?.SpecBoard,
			CurrentVersion: currentVersion,
			Retrievers: retrievers);
	}

	// Hybrid candidate pool: Class-A lexical floor ⊕ Class-B vectors, RRF-fused with
	// provenance by the facade. Entity address is (Scope=project, Type=board, Id=slug); the
	// board filter is applied at the index level (SearchFilter(Type=board)). No embedder →
	// the vector index is simply absent (semantic=false, not degraded); a query-time embed
	// failure is caught by the facade and flagged degraded.
	async Task<(List<TaskSearchHit> Hits, SearchRetrievers Retrievers)> HybridCandidatesAsync(
		string projectKey, string query, string? boardFilter, int k, string? urlPrefix, MethodologyRuntime runtime, CancellationToken ct)
	{
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		await EnsureLexicalBackfillAsync(ctx, projectKey, runtime, ct);

		Func<DataConnection> connect = () => _boards.NewEnsuredConnection(projectKey);
		var indexes = new List<ISearchIndex> { new SqliteFtsIndex(connect) };
		if (_llm is not null)
			indexes.Add(new VectorSearchIndex(connect, new LlmClientEmbedder(_llm, projectKey), VectorDim));

		var resp = await new SearchService(indexes).SearchAsync(projectKey, query, new SearchFilter(boardFilter), k, ct);
		if (resp.Hits.Count == 0) return ([], resp.Retrievers);

		// Semantic-noise floor (spec search-relevance-floor): a hit the lexical leg did NOT confirm
		// must clear the fused RRF floor to survive — the limit is a ceiling, not a plan, so the
		// vector-only tail is cut here rather than padded out. A lexically-confirmed hit bypasses
		// the floor (the lexical leg vouched for it); floor <= 0 disables.
		var floor = _rerank.Floor.SemanticFloor;
		var floored = resp.Hits.Where(h => h.Retriever == "lexical" || floor <= 0 || h.Score >= floor).ToList();
		if (floored.Count == 0) return ([], resp.Retrievers);

		// Resolve comment hits (tasks-search-comments): a doc with Id "c:<key>" is a COMMENT, not
		// a node — map its key to the owner node (one query over the ACTIVE comment rows) and
		// surface the OWNER node row. A comment doc keeps Type=board, so it lands in the owning
		// board's group below like any node hit.
		var commentKeys = floored
			.Where(h => h.Id.StartsWith(TasksSearchDocs.CommentIdPrefix, StringComparison.Ordinal))
			.Select(h => h.Id[TasksSearchDocs.CommentIdPrefix.Length..]).Distinct().ToList();
		var ownerByComment = commentKeys.Count == 0
			? new Dictionary<string, string>()
			: (await ctx.GetTable<CommentRow>()
					.Where(c => commentKeys.Contains(c.Key) && c.ActiveTo == null)
					.Select(c => new { c.Key, c.NodeId }).ToListAsync(ct))
				.ToDictionary(c => c.Key, c => c.NodeId, StringComparer.Ordinal);

		// Hits carry Type=board, Id=slug (nodes) or "c:"+commentKey (comments) — group by board,
		// LEAN-project each owning board's OPEN nodes once (no relation panel — query wire is
		// lean), resolve each hit to a node row and thread fused score/retriever + MatchedIn.
		// Fused rank is the hit's index in `floored`; a group yields ascending rank, so the FIRST
		// time a (board, slug) target appears WINS its rank.
		var ranked = new List<(int Rank, TaskSearchHit Hit)>();
		var seen = new HashSet<(string Board, string Slug)>();
		foreach (var g in floored.Select((h, rank) => (h, rank)).GroupBy(x => x.h.Type, StringComparer.Ordinal))
		{
			// A hit's Type is its owning board. A deleted board can still leave orphan search docs
			// (pre-fix orphans / delete races) — skip the stale group instead of throwing.
			var meta = await _boards.FindAsync(projectKey, g.Key, ct);
			if (meta is null) continue;
			var (bySlug, byNodeId) = await ProjectBoardLeanOpenAsync(projectKey, g.Key, meta.Kind, runtime, urlPrefix, ct);
			foreach (var (h, rank) in g)
			{
				var isComment = h.Id.StartsWith(TasksSearchDocs.CommentIdPrefix, StringComparison.Ordinal);
				PlanNodeView? node;
				if (isComment)
				{
					// A comment resolves to its owner in the SAME open view; if the owner is
					// absent (closed/terminal — the node's FTS row is gone but the comment row
					// lingered) the hit is dropped. This staleness is bounded and harmless.
					var key = h.Id[TasksSearchDocs.CommentIdPrefix.Length..];
					node = ownerByComment.TryGetValue(key, out var nodeId) && byNodeId.TryGetValue(nodeId, out var owner) ? owner : null;
				}
				else
				{
					node = bySlug.GetValueOrDefault(h.Id);
				}
				if (node is null) continue;
				if (!seen.Add((g.Key, node.Key))) continue; // target already surfaced at a better rank
				ranked.Add((rank, new TaskSearchHit(g.Key, node, h.Score, h.Retriever, isComment ? "comment" : null)));
			}
		}
		return (ranked.OrderBy(x => x.Rank).Select(x => x.Hit).ToList(), resp.Retrievers);
	}

	// Lean OPEN-set projection for query-mode hit resolve: active non-terminal nodes + tags,
	// no per-node relation ListAsync / delivery / lineage. Same open pool HybridCandidates
	// previously took from GetAsync(includeClosed: false) minus the terminal-ancestor keep
	// (query hits never address pure terminal ancestors via FTS).
	async Task<(Dictionary<string, PlanNodeView> BySlug, Dictionary<string, PlanNodeView> ByNodeId)>
		ProjectBoardLeanOpenAsync(
			string projectKey, string board, string kindSlug, MethodologyRuntime runtime,
			string? urlPrefix, CancellationToken ct)
	{
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var open = ctx.PlanNodes
			.Where(n => n.Board == board && n.ActiveTo == null)
			.ToList()
			.Where(n => !runtime.IsTerminalStatus(kindSlug, n.Status))
			.ToList();
		var tagsByNode = await _tags.BoardTagsAsync(projectKey, board, ct);
		return TaskSearchProjector.LeanIndex(board, open, tagsByNode, urlPrefix);
	}

	// Final ordering of the selected set. No sort: query mode keeps the fused relevance
	// order, a listing defaults to priority-then-key (the board's canonical order). An
	// explicit sort reorders WITHIN the selected set (with a query the selection stays
	// relevance-driven — only the presentation order changes); Relevance = keep the fused
	// order (`desc` is meaningless there and ignored). Created/Updated read the active
	// revision's temporal columns; ties break on key then board for determinism.
	List<TaskSearchHit> SortHits(string projectKey, List<TaskSearchHit> hits, (TaskSortBy By, bool Desc)? sort, bool hasQuery)
	{
		if (sort is null)
			return hasQuery ? hits : Ordered(hits, h => h.Node.Priority, desc: false);
		var (by, desc) = sort.Value;
		switch (by)
		{
			case TaskSortBy.Relevance:
				return hits; // guarded to query mode; the fused order IS relevance
			case TaskSortBy.Priority:
				return Ordered(hits, h => h.Node.Priority, desc);
			case TaskSortBy.Title:
				return Ordered(hits, h => h.Node.Title, desc, StringComparer.OrdinalIgnoreCase);
			case TaskSortBy.Created:
			case TaskSortBy.Updated:
				var times = NodeTimes(projectKey);
				return by == TaskSortBy.Created
					? Ordered(hits, h => times.GetValueOrDefault(h.Node.NodeId).Created, desc)
					: Ordered(hits, h => times.GetValueOrDefault(h.Node.NodeId).Updated, desc);
			default:
				throw new ArgumentException($"unknown sort '{by}'");
		}
	}

	static List<TaskSearchHit> Ordered<TKey>(List<TaskSearchHit> hits, Func<TaskSearchHit, TKey> key, bool desc, IComparer<TKey>? cmp = null) =>
		(desc ? hits.OrderByDescending(key, cmp) : hits.OrderBy(key, cmp))
			.ThenBy(h => h.Node.Key, StringComparer.Ordinal)
			.ThenBy(h => h.Board, StringComparer.Ordinal)
			.ToList();

	// NodeId -> (Created, Updated) of the ACTIVE revisions, project-wide — the sort axes
	// the view rows don't carry (loaded only when a created/updated sort asks for it).
	Dictionary<string, (DateTime Created, DateTime Updated)> NodeTimes(string projectKey)
	{
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		return ctx.PlanNodes.Where(n => n.ActiveTo == null).ToList()
			.Where(n => n.NodeId.Length > 0)
			.ToDictionary(n => n.NodeId, n => (n.Created, n.Updated), StringComparer.Ordinal);
	}

	// READ snippet: bodyLen <= 0 -> the full body; otherwise the first N chars with "…"
	// appended when cut (mirrors ModuleMcp.SnippetBody — read returns content by default).
	static string SnippetBody(string body, int bodyLen) =>
		bodyLen <= 0 || body.Length <= bodyLen ? body : string.Concat(body.AsSpan(0, bodyLen), "…");

	// Read-merge a patch against the prior active row: a field omitted from the patch
	// (null) inherits the prior value; a non-null value sets it ("" clears it).
	static PlanNode Merge(NodePatch p, IReadOnlyDictionary<string, PlanNode> prior)
	{
		var cur = prior.GetValueOrDefault(p.Key) ?? (p.PrevKey is not null ? prior.GetValueOrDefault(p.PrevKey) : null);
		return new PlanNode
		{
			Key = p.Key,
			Version = p.Version,
			Status = p.Status ?? cur?.Status ?? string.Empty,
			Type = (p.Type ?? cur?.Type ?? string.Empty).ToLowerInvariant(),
			Name = p.Title ?? cur?.Name ?? string.Empty,
			Body = p.Body ?? cur?.Body ?? string.Empty,
			Priority = p.Priority ?? cur?.Priority ?? 0,
			PrevKey = p.PrevKey,
		};
	}

	static Dictionary<string, string> LinkFields(IReadOnlyList<NodePatch> nodes, Func<NodePatch, string?> pick)
	{
		var map = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var p in nodes)
		{
			var v = pick(p);
			if (!string.IsNullOrWhiteSpace(v)) map[p.Key] = v!;
		}
		return map;
	}

	// DATA-DRIVEN transition gate (idea-review-needs-plan generalized): a transition whose
	// definition names a PreconditionArtifact requires an active `artifact:<slug>` comment
	// on the node before it fires — the ideas preset gates exploring→review on
	// `artifact:spec_plan` (the spec-update plan), a definition kind declares its own.
	// WorkflowEngine stays pure (kind/type/from/to); this guard lives here because it
	// reads comments (ICommentService). Mirrors WorkflowEngine's from-resolution
	// (unchanged status skipped, unknown prior = recovery); landing DIRECTLY in a gated
	// target status at creation is refused too, so the gate can't be bypassed by birth.
	// NodeId comes from the prior row (desired rows get their NodeId assigned inside the
	// temporal upsert, after this check).
	async Task RequirePreconditionArtifactsAsync(
		MethodologyRuntime runtime, string? kindSlug, string projectKey, string board,
		PlanNode[] desired, Dictionary<string, PlanNode> prior, CancellationToken ct)
	{
		foreach (var d in desired)
		{
			var wf = runtime.For(kindSlug, d.Type.Length == 0 ? null : d.Type);
			if (wf is null) continue; // ApplyWorkflow already rejected the unknown type
			var p = prior.GetValueOrDefault(d.Key) ?? (d.PrevKey is not null ? prior.GetValueOrDefault(d.PrevKey) : null);
			var from = p?.Status;
			if (from is not null && string.Equals(from, d.Status, StringComparison.OrdinalIgnoreCase)) continue; // unchanged
			if (from is not null && wf.Status(from) is null) from = null; // recovery — mirrors WorkflowEngine

			string? artifact;
			string transition;
			if (from is null)
			{
				// No transition fired (creation/recovery) — but if any transition INTO this
				// status is gated, entering it directly must satisfy the same artifact.
				var gated = wf.Transitions.FirstOrDefault(t =>
					t.PreconditionArtifact is not null && string.Equals(t.To, d.Status, StringComparison.OrdinalIgnoreCase));
				if (gated is null) continue;
				artifact = gated.PreconditionArtifact;
				transition = $"'{gated.From}' -> '{gated.To}'";
			}
			else
			{
				var tr = wf.Transition(from, d.Status);
				if (tr?.PreconditionArtifact is null) continue;
				artifact = tr.PreconditionArtifact;
				transition = $"'{from}' -> '{d.Status}'";
			}

			var tag = $"artifact:{artifact}";
			if (p is null || p.NodeId.Length == 0)
				throw new InvalidOperationException(
					$"node '{d.Key}' can't be created directly in '{d.Status}' — transition {transition} requires an {tag} comment; create the node, add the comment, then transition");
			var comments = await _comments.ListForNodeAsync(projectKey, board, p.NodeId, ct);
			if (!comments.Any(c => c.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))))
				throw new InvalidOperationException(
					$"transition {transition} on node '{d.Key}' requires an {tag} comment (the transition's precondition artifact) — add the comment, then retry");
		}
	}

	// Default status, assign/carry the stable NodeId (new = fresh, edit = keep, rename =
	// inherit from source), and validate status/transition — the single workflow point.
	// The workflow resolves through the runtime: definition-declared kinds from data,
	// everything else from the built-in presets exactly as before.
	// `reason` is the call-scoped RequiresReason payload (NodePatch.Reason) — never the body.
	static PlanNode ApplyWorkflow(MethodologyRuntime runtime, string? kindSlug, PlanNode node, Dictionary<string, PlanNode> prior, TasksActor actor, string? reason)
	{
		var type = node.Type.Length == 0 ? null : node.Type;
		// Simple boards: type is a label from a small fixed set; empty defaults to `task`, and an
		// out-of-set type is rejected. (Work validates type via For(); Simple's For() ignores type,
		// so the vocabulary is enforced here. Definition kinds validate type via For(), like Work.)
		if (runtime.PresetKind(kindSlug) == BoardKind.Simple)
		{
			type ??= "task";
			if (!MethodologyPresets.SimpleTypes.Contains(type))
				throw new ArgumentException($"invalid type '{type}' for a simple board; valid: {MethodologyPresets.ValidTypes(BoardKind.Simple)}");
		}
		var wf = runtime.For(kindSlug, type);
		// Materialize the resolved default type at WRITE time (spec quick-add-stores-default-
		// type): a single-FSM kind (every preset kind but Work — ideas/spec/intake/classic)
		// treats an empty type as its declared default for RESOLUTION (`wf` is non-null here
		// even though `type` is still null), but until now only READS re-derived that default
		// (RequireDefinitionLinks, below) — the STORED row (and so the UI) kept the empty
		// string the caller sent. Persist the same default the resolution already uses, so the
		// store/UI agree with it. Work and a definition-declared kind still demand an explicit
		// type (`wf` stays null on an empty type there — WorkflowEngine.Validate rejects the
		// write below, unchanged).
		if (type is null && wf is not null) type = runtime.DefaultType(kindSlug);
		if (type is not null) node = node with { Type = type };
		var n = node.Status.Length > 0 ? node : node with { Status = wf?.Initial ?? "Pending" };

		var current = prior.GetValueOrDefault(n.Key);
		var source = n.PrevKey is not null ? prior.GetValueOrDefault(n.PrevKey) : null;
		var nodeId = current?.NodeId is { Length: > 0 } cid ? cid
			: source?.NodeId is { Length: > 0 } sid ? sid
			: Guid.NewGuid().ToString("N");
		n = n with { NodeId = nodeId };

		var from = current?.Status ?? source?.Status;
		// Per-transition enforcement only (the global enforceApproval flag stays off): a
		// transition whose methodology declares EnforceApproval demands an approving actor.
		// RequiresReason is gated on the call-scoped `reason` field — never the node body.
		var res = WorkflowEngine.Validate(wf, runtime.KindName(kindSlug), runtime.ValidTypes(kindSlug),
			type, from, n.Status, actorCanApprove: actor.CanApprove, hasReason: !string.IsNullOrWhiteSpace(reason));
		if (!res.Ok) throw new ArgumentException(res.Error);
		return n;
	}

	// Persist a RequiresReason transition's `reason` field as an `artifact:reason` comment on
	// the node. Best-effort post-write (same style as tags/partOf associations): only fires
	// when the applied transition actually required a reason and the patch carried one.
	async Task PersistReasonCommentsAsync(
		string projectKey, string board, MethodologyRuntime runtime, string? kindSlug,
		IReadOnlyList<NodePatch> patches, PlanNode[] desired, Dictionary<string, PlanNode> prior, CancellationToken ct)
	{
		foreach (var p in patches)
		{
			if (string.IsNullOrWhiteSpace(p.Reason)) continue;
			var d = desired.FirstOrDefault(n => string.Equals(n.Key, p.Key, StringComparison.Ordinal));
			if (d is null || d.NodeId.Length == 0) continue;

			var cur = prior.GetValueOrDefault(d.Key)
				?? (d.PrevKey is not null ? prior.GetValueOrDefault(d.PrevKey) : null);
			var from = cur?.Status;
			if (from is null) continue; // birth — RequiresReason only applies to transitions
			if (string.Equals(from, d.Status, StringComparison.OrdinalIgnoreCase)) continue;

			var wf = runtime.For(kindSlug, d.Type.Length == 0 ? null : d.Type);
			var tr = wf?.Transition(from, d.Status);
			if (tr is null || !tr.RequiresReason) continue;

			await _comments.AddAsync(projectKey, board, d.NodeId, parentId: null, author: "system",
				body: p.Reason.Trim(), tags: ["artifact:reason"], ct);
		}
	}

	// Declarative immutable-field invariants (NodeId/type once set). The prior row is the
	// active node at this key, or — for a rename — at its PrevKey; null means a new node.
	static void ValidateChanges(PlanNode[] desired, Dictionary<string, PlanNode> prior)
	{
		foreach (var n in desired)
		{
			var old = prior.GetValueOrDefault(n.Key)
				?? (n.PrevKey is not null ? prior.GetValueOrDefault(n.PrevKey) : null);
			var result = ChangeValidator.Validate(new EntityChange<PlanNode>(old, n));
			if (!result.IsValid)
				throw new ArgumentException(result.Errors[0].ErrorMessage);
		}
	}

	// specRef accepts the spec node's slug OR its NodeId, mirroring part_of (ResolveParentId).
	// A slug resolves against the board's linked spec board (SpecBoard); the returned map holds
	// NodeIds only. NodeId-shaped values pass through untouched (existing behavior).
	Dictionary<string, string> ResolveSpecRefs(string projectKey, TaskBoardMeta board, Dictionary<string, string> specRefs)
	{
		if (specRefs.Count == 0) return specRefs;
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		foreach (var (key, raw) in specRefs.ToList())
		{
			var v = raw.Trim();
			if (NodeRefResolver.LooksLikeNodeId(v)) { specRefs[key] = v; continue; }
			if (board.SpecBoard is not { Length: > 0 } sb)
				throw new ArgumentException($"specRef '{raw}' (node '{key}') is a slug, but this board has no linked spec board — provide the spec node's NodeId");
			var slug = v.ToLowerInvariant();
			var target = ctx.PlanNodes.Where(n => n.Board == sb && n.ActiveTo == null && n.Key == slug).ToList().FirstOrDefault();
			if (target is null || target.NodeId.Length == 0)
				throw new ArgumentException($"specRef '{raw}' (node '{key}') does not match any node on spec board '{sb}'");
			specRefs[key] = target.NodeId;
		}
		return specRefs;
	}

	// blockedBy accepts the blocker's slug OR its NodeId, mirroring specRef (ResolveSpecRefs).
	// A slug resolves on THIS board (blockers are usually siblings) over the active rows
	// overlaid with this batch, so a blocker created in the same call resolves too. The
	// returned map holds NodeIds only; NodeId-shaped values pass through untouched.
	static Dictionary<string, string> ResolveBlockedBy(
		string board, PlanNode[] desired, IReadOnlyDictionary<string, PlanNode> prior, Dictionary<string, string> blockedBy)
	{
		if (blockedBy.Count == 0) return blockedBy;
		var slugToId = prior.Values.Where(n => n.NodeId.Length > 0)
			.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		foreach (var n in desired.Where(n => n.NodeId.Length > 0))
			slugToId[n.Key] = n.NodeId;
		foreach (var (key, raw) in blockedBy.ToList())
		{
			var v = raw.Trim();
			if (NodeRefResolver.LooksLikeNodeId(v)) { blockedBy[key] = v; continue; }
			if (!slugToId.TryGetValue(v.ToLowerInvariant(), out var id))
				throw new ArgumentException($"blockedBy '{raw}' (node '{key}') does not match any node on board '{board}' — a blocker's slug resolves on the same board; pass a NodeId to reference a node on another board");
			blockedBy[key] = id;
		}
		return blockedBy;
	}

	// DATA-DRIVEN link-target guard (schema v2, was ValidateSpecRefsAsync +
	// RequireAcceptedIdeaForSpecAsync): every PROVIDED ref must resolve to a real node, and
	// when the writing kind's constraints declare a target for that link kind (TargetKind /
	// TargetStatuses), the target's EFFECTIVE kind and status must match. The target rule is
	// KIND-level, not type-level: the constraint's Type says who MUST carry the link, its
	// target says what the link points at for this kind — so a voluntary ref (e.g. a chore's
	// specRef) is validated by the same rule. The SpecBoard binding stays alongside: it is
	// the quartet auto-wire's board pin, data on the board meta rather than the methodology.
	async Task ValidateLinkTargetsAsync(
		string projectKey, TaskBoardMeta board, string? kindSlug, MethodologyRuntime runtime,
		Dictionary<string, string> specRefs, Dictionary<string, string> ideaRefs, CancellationToken ct)
	{
		if (specRefs.Count == 0 && ideaRefs.Count == 0) return;
		var index = await BuildNodeIndexAsync(projectKey, ct);
		var constraints = runtime.LinkConstraints(kindSlug);

		ValidateRefs("specRef", "task_spec", specRefs);
		ValidateRefs("ideaRef", "idea_spec", ideaRefs);

		// Auto-wire pin: when this board links a specific spec board, every specRef must
		// live on it (same message as always).
		if (board.SpecBoard is { Length: > 0 } sb)
			foreach (var (key, refId) in specRefs)
				if (index.TryGetValue(refId, out var t) && t.Board != sb)
					throw new ArgumentException($"specRef '{refId}' (node '{key}') is on board '{t.Board}', but this work board links spec board '{sb}'");

		void ValidateRefs(string field, string link, Dictionary<string, string> refs)
		{
			if (refs.Count == 0) return;
			// The kind's target rule for this link kind: the first constraint declaring one
			// (the validator keeps (type, link) pairs unique; quartet declares one rule per link).
			var rule = constraints.FirstOrDefault(c =>
				string.Equals(c.Link, link, StringComparison.OrdinalIgnoreCase)
				&& (c.TargetKind is not null || c.TargetStatuses is { Count: > 0 }));
			foreach (var (key, refId) in refs)
			{
				if (!index.TryGetValue(refId, out var t))
					throw new ArgumentException($"{field} '{refId}' (node '{key}') does not resolve to any node");
				if (rule is null) continue;
				if (rule.TargetKind is not null && !string.Equals(runtime.KindName(t.BoardKind), rule.TargetKind, StringComparison.OrdinalIgnoreCase))
					throw new ArgumentException($"{field} '{refId}' (node '{key}') points to board '{t.Board}', which is not a {rule.TargetKind} board");
				if (rule.TargetStatuses is { Count: > 0 } ts && !ts.Contains(t.Status, StringComparer.OrdinalIgnoreCase))
					throw new ArgumentException($"{field} '{refId}' (node '{key}') target is '{t.Status}', not {string.Join("|", ts)} — a {runtime.KindName(kindSlug)} change needs a {rule.TargetKind ?? link} node in status {string.Join("|", ts)}");
			}
		}
	}

	// NodeIds of this node's part_of children whose own row is still active. Terminal-status
	// children count too — they are active rows and would dangle just the same.
	async Task<IReadOnlyList<string>> ActivePartOfChildrenAsync(string projectKey, string nodeId, CancellationToken ct)
	{
		if (nodeId.Length == 0) return [];
		var childIds = (await _relations.ListAsync(projectKey, nodeId, "to", ct: ct))
			.Where(e => e.Kind == "part_of").Select(e => e.FromNodeId).ToList();
		if (childIds.Count == 0) return [];
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		return childIds.Where(id => ctx.PlanNodes.Any(n => n.ActiveTo == null && n.NodeId == id)).ToList();
	}

	// Create relation edges from a per-node field after the upsert applies. task_spec
	// (specRef): task -> spec. blocks (blockedBy): blocker -> task. Idempotent.
	async Task LinkRefsAsync(string projectKey, string kind, PlanNode[] desired, Dictionary<string, string> refs, bool blockerIsFrom, CancellationToken ct)
	{
		if (refs.Count == 0) return;
		var byKey = desired.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		foreach (var (key, other) in refs)
			if (byKey.TryGetValue(key, out var nid) && nid.Length > 0)
			{
				var (from, to) = blockerIsFrom ? (other, nid) : (nid, other);
				await _relations.CreateAsync(projectKey, kind, from, to, ct);
			}
	}

	// Active (ValidTo == null) commits for a whole board, nodeId -> sorted sha list.
	static async Task<Dictionary<string, List<string>>> BoardCommitsAsync(TasksDb ctx, string board, CancellationToken ct)
	{
		var rows = await ctx.PlanNodeCommits.Where(c => c.Board == board && c.ValidTo == null)
			.Select(c => new { c.NodeId, c.Sha }).ToListAsync(ct);
		return rows.GroupBy(r => r.NodeId, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.Select(x => x.Sha).OrderBy(s => s, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
	}

	// Populate PlanNode.Commits (the NotColumn enrichment field) for an echo batch so the
	// write-ack / delta projection carries a node's attached commits.
	static async Task<IReadOnlyList<PlanNode>> AttachCommitsAsync(TasksDb ctx, string board, IReadOnlyList<PlanNode> nodes, CancellationToken ct)
	{
		if (nodes.Count == 0) return nodes;
		var byNode = await BoardCommitsAsync(ctx, board, ct);
		return nodes.Select(n => n with { Commits = byNode.TryGetValue(n.NodeId, out var cs) ? cs : [] }).ToList();
	}

	// nodeId -> its active commit set (reverse lookup + attach helper), read on ctx.
	static async Task<HashSet<string>> NodesCarryingCommitAsync(TasksDb ctx, string commit, CancellationToken ct)
	{
		var v = (commit ?? "").Trim().ToLowerInvariant();
		if (v.Length == 0) return [];
		// EXACT match always; PREFIX match on a stored full sha only when the query is a >=7
		// hex short id (a short query finds the long commit — spec: prefix match on stored value).
		var prefixable = v.Length >= 7 && v.All(Uri.IsHexDigit);
		var rows = await ctx.PlanNodeCommits
			.Where(c => c.ValidTo == null && (c.Sha == v || (prefixable && c.Sha.StartsWith(v))))
			.Select(c => c.NodeId).ToListAsync(ct);
		return rows.ToHashSet(StringComparer.Ordinal);
	}

	// Invariant: a work task in `Blocked` must name a blocker (blockedBy in this call, or
	// an already-active `blocks` edge into it). "Blocked requires a link."
	async Task RequireBlockersAsync(BoardKind? kind, string projectKey, PlanNode[] desired, Dictionary<string, string> blockedBy, CancellationToken ct)
	{
		if (kind != BoardKind.Work) return;
		foreach (var n in desired)
		{
			if (!string.Equals(n.Status, "Blocked", StringComparison.OrdinalIgnoreCase)) continue;
			if (blockedBy.ContainsKey(n.Key)) continue;
			var hasActiveBlocker = (await _relations.ListAsync(projectKey, n.NodeId, "to", ct: ct)).Any(e => e.Kind == "blocks");
			if (!hasActiveBlocker)
				throw new ArgumentException($"a Blocked task must name a blocker — provide blockedBy (node '{n.Key}')");
		}
	}

	// Leaving Blocked manually closes the active `blocks` edges into the node (history kept).
	async Task CloseBlocksOnLeaveAsync(string projectKey, PlanNode[] desired, Dictionary<string, PlanNode> prior, CancellationToken ct)
	{
		foreach (var n in desired)
		{
			var wasBlocked = prior.TryGetValue(n.Key, out var cur) && string.Equals(cur.Status, "Blocked", StringComparison.OrdinalIgnoreCase);
			if (!wasBlocked || string.Equals(n.Status, "Blocked", StringComparison.OrdinalIgnoreCase)) continue;
			foreach (var e in (await _relations.ListAsync(projectKey, n.NodeId, "to", ct: ct)).Where(e => e.Kind == "blocks"))
				await _relations.CloseAsync(projectKey, "blocks", e.FromNodeId, e.ToNodeId, ct);
		}
	}

	// DATA-DRIVEN link constraints (primitives-link-constraints): a NEW node of a
	// constrained type must carry the constrained link in THIS call (task_spec = specRef,
	// blocks = blockedBy, idea_spec = ideaRef — the validator admits only these).
	// CADENCE follows the builtin link kind's semantics: task_spec/blocks are STRUCTURAL
	// edges, required at creation only (edits don't re-require them); idea_spec is a
	// PROVENANCE edge — every write of a constrained type must name the idea that
	// authorizes it (the spec-write governance, now as data). Constraints resolve through
	// the runtime for preset and definition kinds alike — the work preset declares
	// feature/bug → task_spec, and `chore` (engineering hygiene below the spec) is exempt
	// because no constraint names it; the spec preset declares spec → idea_spec.
	static void RequireDefinitionLinks(
		MethodologyRuntime runtime, string? kindSlug, PlanNode[] desired,
		Dictionary<string, PlanNode> prior,
		Dictionary<string, string> specRefs, Dictionary<string, string> blockedBy, Dictionary<string, string> ideaRefs)
	{
		var constraints = runtime.LinkConstraints(kindSlug);
		if (constraints.Count == 0) return;
		foreach (var n in desired)
		{
			var isNew = !prior.ContainsKey(n.Key) && (n.PrevKey is null || !prior.ContainsKey(n.PrevKey));
			// The node's EFFECTIVE type: single-FSM preset kinds accept untyped nodes (an
			// untyped spec node IS a `spec`), so an empty type resolves to the kind's default
			// before constraint matching — the old kind-wide guard's reach, as data.
			var type = n.Type.Length == 0 ? runtime.DefaultType(kindSlug) : n.Type;
			foreach (var c in constraints)
			{
				if (!string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase)) continue;
				var everyWrite = string.Equals(c.Link, "idea_spec", StringComparison.OrdinalIgnoreCase);
				if (!isNew && !everyWrite) continue;
				var kindName = runtime.KindName(kindSlug);
				// task_spec keeps the historical, target-naming wording; idea_spec states the
				// provenance rule with the declared target; the generic form names the link
				// kind + the upsert field that expresses it.
				var message = c.Link.ToLowerInvariant() switch
				{
					"task_spec" when !specRefs.ContainsKey(n.Key) =>
						$"a {kindName} {n.Type} must link a {c.TargetKind ?? "spec"} node — provide specRef (node '{n.Key}')",
					"blocks" when !blockedBy.ContainsKey(n.Key) =>
						$"a {kindName} {n.Type} must carry a {c.Link} link at creation — provide blockedBy (node '{n.Key}')",
					// idea_spec — the validator admits no other kind
					"idea_spec" when !ideaRefs.ContainsKey(n.Key) =>
						c.TargetStatuses is { Count: > 0 } ts
							? $"a {kindName} change must be made under {string.Join("|", ts)} {c.TargetKind ?? c.Link} — provide ideaRef (node '{n.Key}')"
							: $"a {kindName} {n.Type} must carry a {c.Link} link on every write — provide ideaRef (node '{n.Key}')",
					_ => null,
				};
				if (message is not null) throw new ArgumentException(message);
			}
		}
	}

	// Active node key -> chain of prior keys it was renamed from.
	static Dictionary<string, List<string>> BuildLineage(List<PlanNode> all)
	{
		var edge = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var g in all.GroupBy(n => n.Key, StringComparer.Ordinal))
		{
			var birth = g.OrderBy(n => n.Version).First();
			if (!string.IsNullOrEmpty(birth.PrevKey))
				edge[g.Key] = birth.PrevKey!;
		}

		var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var key in edge.Keys)
		{
			var chain = new List<string>();
			var cur = key;
			var guard = 0;
			while (edge.TryGetValue(cur, out var prev) && guard++ < 1000)
			{
				chain.Add(prev);
				cur = prev;
			}
			result[key] = chain;
		}
		return result;
	}

	// ---- UI quick-add ----

	public async Task QuickAddAsync(string projectKey, string board, string name, string? body, long priority, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(name)) return;

		// Status/type must fit the board's kind — a cold "Pending" is invalid on kinded
		// boards (ideas/spec/intake). One convention for preset and definition kinds alike:
		// quick-add writes the kind's DEFAULT TYPE (first type of the first block — ideas→
		// idea, spec→spec, intake→issue, simple→task) in that workflow's initial status.
		var meta = await _boards.FindAsync(projectKey, board, ct);
		var runtime = await RuntimeAsync(projectKey, ct);
		var type = runtime.DefaultType(meta?.Kind);
		var status = runtime.For(meta?.Kind, type)?.Initial ?? "Pending";

		var key = GenKey(name);
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		// Assign a stable NodeId so the node can be linked (relations/specRef) later.
		await TemporalStore.UpsertAsync(ctx, new[]
		{
			new PlanNode { Board = board, Key = key, NodeId = Guid.NewGuid().ToString("N"), Version = 0, Status = status, Type = type, Name = name.Trim(), Body = body?.Trim() ?? string.Empty, Priority = priority },
		}, partition: n => n.Board == board, ct: ct);
		await _boards.TouchAsync(projectKey, board, ct);
	}

	static string GenKey(string name)
	{
		var ascii = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
		if (ascii.Length == 0 || !char.IsLetter(ascii[0])) ascii = "task-" + ascii;
		ascii = ascii.Trim('-');
		if (ascii.Length > 32) ascii = ascii[..32].Trim('-');
		return $"{ascii}-{Guid.NewGuid():N}"[..(Math.Min(ascii.Length, 32) + 7)];
	}

	// ---- system: report_issue ----

	public async Task<string> ReportIssueAsync(string project, string board, string title, string body, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title is required");
		var key = IssueSlug(title);
		await _boards.EnsureAsync(project, board, ct); // auto-create the triage board on first report
		using var ctx = _boards.NewEnsuredConnection(project);
		var r = await TemporalStore.UpsertAsync(ctx, new[]
		{
			// Assign a stable NodeId here: this path writes straight to TemporalStore and skips
			// ApplyWorkflow (the usual NodeId-assignment point), so without this the row lands with
			// an empty NodeId and the /tasks/{board}/{slug} permalink 404s (slug→NodeId→GetNode).
			// The issues board is a `simple` board → its preset vocab (Todo|InProgress|…), not the
			// intake `reported`. Type `issue` is in the simple type set.
			new PlanNode { Board = board, Key = key, NodeId = Guid.NewGuid().ToString("N"), Version = 0, Status = "Todo", Type = "issue", Name = title.Trim(), Body = body, Priority = 0 },
		}, partition: n => n.Board == board, ct: ct);
		if (r.Applied) await _boards.TouchAsync(project, board, ct);
		return key;
	}

	// ---- search seam: Class-A FTS tag refresh + lexical backfill ----

	// Re-index this batch's OPEN nodes with their now-current tags. The in-tx write (onWithinTx in
	// UpsertAsync) already committed content + membership transactionally with pre-upsert tags; this
	// post-commit pass reflects SetTagsAsync into the FTS Tags column. Targeted (not a wholesale
	// board rebuild) so it only re-writes the changed nodes and never empties the board's index.
	static async Task RefreshFtsTagsAsync(TasksDb ctx, string projectKey, string board, IReadOnlyList<PlanNode> desired, MethodologyRuntime runtime, CancellationToken ct)
	{
		var open = desired.Where(n => TasksSearchDocs.IsIndexable(n, runtime)).ToList();
		if (open.Count == 0) return;
		var tags = await NodeTagsAsync(ctx, board, open.Select(n => n.NodeId), ct);
		var fts = new SqliteFtsIndex(() => ctx);
		using var tx = await ctx.BeginTransactionAsync(ct);
		try
		{
			foreach (var n in open)
				await fts.IndexAsync(ctx, TasksSearchDocs.ToDoc(n, projectKey, tags.GetValueOrDefault(n.NodeId, [])), ct);
			await tx.CommitAsync(ct);
		}
		catch
		{
			await tx.RollbackAsync(ct);
			throw;
		}
	}

	// One-time lexical backfill: content written before a search retrofit has no search_fts rows.
	// TWO independently-guarded passes both run: nodes (for a file predating the node retrofit) and
	// comments (for a file predating tasks-search-comments — its nodes are already indexed, so the
	// node guard alone would never re-run). Cheap, count-guarded, at most once per project file each.
	static async Task EnsureLexicalBackfillAsync(TasksDb ctx, string scope, MethodologyRuntime runtime, CancellationToken ct)
	{
		await BackfillNodesAsync(ctx, scope, runtime, ct);
		await BackfillCommentsAsync(ctx, scope, runtime, ct);
	}

	// Node backfill: rebuilds the OPEN set across every board from the same projection the write
	// seam uses. Guard is ANY search_fts row (node or comment) — a virgin file is the only re-run.
	static async Task BackfillNodesAsync(TasksDb ctx, string scope, MethodologyRuntime runtime, CancellationToken ct)
	{
		if (ctx.Execute<long>("SELECT count(*) FROM search_fts") > 0) return;
		var open = ctx.PlanNodes.Where(n => n.ActiveTo == null).ToList()
			.Where(n => TasksSearchDocs.IsIndexable(n, runtime)).ToList();
		if (open.Count == 0) return;

		var tagsByNode = (await ctx.GetTable<NodeTag>().Where(t => t.ValidTo == null)
				.Select(t => new { t.NodeId, t.Tag }).ToListAsync(ct))
			.GroupBy(r => r.NodeId).ToDictionary(g => g.Key, g => g.Select(x => x.Tag).ToList());

		var fts = new SqliteFtsIndex(() => ctx);
		using var tx = await ctx.BeginTransactionAsync(ct);
		try
		{
			foreach (var n in open)
				await fts.IndexAsync(ctx, TasksSearchDocs.ToDoc(n, scope, tagsByNode.GetValueOrDefault(n.NodeId, [])), ct);
			await tx.CommitAsync(ct);
		}
		catch
		{
			await tx.RollbackAsync(ct);
			throw;
		}
	}

	// Comment backfill: own guard (no "c:%" row yet) so it runs on files whose nodes are already
	// indexed. Indexes every ACTIVE comment whose owner node is currently indexable (resolve owner
	// via PlanNodes by NodeId) — a comment under a closed/terminal owner is dropped at read time
	// anyway. Empty-comment files re-run the cheap count each search, matching the node guard's cost.
	static async Task BackfillCommentsAsync(TasksDb ctx, string scope, MethodologyRuntime runtime, CancellationToken ct)
	{
		if (ctx.Execute<long>("SELECT count(*) FROM search_fts WHERE Id LIKE 'c:%'") > 0) return;
		var comments = await ctx.GetTable<CommentRow>().Where(c => c.ActiveTo == null).ToListAsync(ct);
		if (comments.Count == 0) return;

		var indexable = ctx.PlanNodes.Where(n => n.ActiveTo == null).ToList()
			.Where(n => TasksSearchDocs.IsIndexable(n, runtime))
			.Select(n => n.NodeId).ToHashSet(StringComparer.Ordinal);
		var toIndex = comments.Where(c => indexable.Contains(c.NodeId)).ToList();
		if (toIndex.Count == 0) return;

		var fts = new SqliteFtsIndex(() => ctx);
		using var tx = await ctx.BeginTransactionAsync(ct);
		try
		{
			foreach (var c in toIndex)
				await fts.IndexAsync(ctx, TasksSearchDocs.CommentToDoc(c, scope), ct);
			await tx.CommitAsync(ct);
		}
		catch
		{
			await tx.RollbackAsync(ct);
			throw;
		}
	}

	// Active (ValidTo == null) tags for the given nodes on a board, read on the supplied connection.
	static async Task<Dictionary<string, List<string>>> NodeTagsAsync(DataConnection db, string board, IEnumerable<string> nodeIds, CancellationToken ct)
	{
		var ids = nodeIds.Distinct().ToList();
		if (ids.Count == 0) return [];
		var rows = await db.GetTable<NodeTag>()
			.Where(t => t.Board == board && t.ValidTo == null && ids.Contains(t.NodeId))
			.Select(t => new { t.NodeId, t.Tag }).ToListAsync(ct);
		return rows.GroupBy(r => r.NodeId).ToDictionary(g => g.Key, g => g.Select(x => x.Tag).ToList());
	}

	[GeneratedRegex("[^a-z0-9]+")]
	private static partial Regex NonSlug();

	static string IssueSlug(string title)
	{
		var s = NonSlug().Replace(title.ToLowerInvariant(), "-").Trim('-');
		if (s.Length == 0 || !char.IsLetter(s[0])) s = "issue-" + s;
		if (s.Length > 32) s = s[..32].Trim('-');
		return $"{s}-{Guid.NewGuid():N}"[..(s.Length + 1 + 6)];
	}
}
