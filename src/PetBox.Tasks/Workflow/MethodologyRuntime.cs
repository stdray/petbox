namespace PetBox.Tasks.Workflow;

// One workflow block of a board's FSM surface: every type slug sharing one state machine
// (the tasks.workflow shape — feature=bug=chore on a work board is ONE block).
public sealed record WorkflowBlock(IReadOnlyList<string> Types, Workflow Workflow);

// The data-driven FSM resolution seam (engine wave 1.2): given a project's optional
// MethodologyDefinition, answers everything the static WorkflowCatalog answers — per KIND
// SLUG (string), because user-defined kinds don't live on the BoardKind enum. MERGE
// semantics: a definition OVERRIDES per kind — a kind slug the definition declares
// resolves from data; every other kind slug falls back to the catalog exactly as before
// (so a project can define one custom kind and keep the quartet). No definition → pure
// catalog. The instance is immutable and cheap: the service loads the definition once per
// call and resolves through this BEFORE building queries (the helpers are sync).
public sealed class MethodologyRuntime
{
	// The no-definition runtime: every answer falls through to the catalog.
	public static readonly MethodologyRuntime CatalogOnly = new(null);

	// Builtin relation kinds with PROCESS meaning (FSM effects / guards key on these):
	// task_spec (specRef), issue_task (intake auto-close), idea_spec (ideaRef), blocks
	// (gating + unblock effect), part_of (decomposition), supersedes (obsoletion).
	public static readonly string[] ProcessRelationKinds = ["task_spec", "issue_task", "idea_spec", "blocks", "part_of", "supersedes"];

	// Builtin NEUTRAL kinds, available to EVERY project: free semantic edges between any
	// nodes — no FSM effects, no process meaning (spec primitives-link-kinds).
	public static readonly string[] NeutralRelationKinds = ["relates_to", "depends_on", "mirrors"];

	readonly Dictionary<string, MethodologyKindDef> _kinds;
	readonly IReadOnlyList<MethodologyLinkKindDef> _linkKinds;
	readonly IReadOnlyList<MethodologyTagAxisDef> _tagAxes;

	public MethodologyRuntime(MethodologyDefinition? definition)
	{
		_kinds = (definition?.Kinds ?? [])
			.ToDictionary(k => k.Kind, k => k, StringComparer.OrdinalIgnoreCase);
		// `?? []` also covers a hand-written stored document with an explicit JSON null.
		_linkKinds = definition?.LinkKinds ?? [];
		_tagAxes = definition?.TagAxes ?? [];
	}

	// Creation link constraints of a kind — definition kinds only; a catalog kind keeps
	// its hardcoded rule (RequireSpecLinks) until the preset becomes data in wave 3.
	public IReadOnlyList<MethodologyLinkConstraintDef> LinkConstraints(string? kindSlug) =>
		kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind)
			? kind.LinkConstraints ?? []
			: [];

	// Declared tag namespaces (definition-level, project-wide). Empty = free-form tags on
	// definition-resolved boards.
	public IReadOnlyList<MethodologyTagAxisDef> TagAxes => _tagAxes;

	// Every relation kind this project accepts: builtin process + neutral kinds plus the
	// definition-declared ones (order = error-message order).
	public IEnumerable<string> KnownRelationKinds() =>
		ProcessRelationKinds
			.Concat(NeutralRelationKinds)
			.Concat(_linkKinds.Select(k => k.Slug));

	public bool IsValidRelationKind(string kind) =>
		KnownRelationKinds().Contains(kind, StringComparer.OrdinalIgnoreCase);

	public bool IsDefinedKind(string? kindSlug) =>
		kindSlug is not null && _kinds.ContainsKey(kindSlug);

	// The catalog preset serving this kind, or null when the definition overrides it —
	// the guard for preset-only behavior (quartet invariants, spec delivery, FSM effects).
	public BoardKind? CatalogKind(string? kindSlug) =>
		IsDefinedKind(kindSlug) ? null : WorkflowCatalog.ParseKind(kindSlug);

	// The kind name a view/response reports: a defined kind verbatim (definition slugs are
	// canonical lowercase), else the parsed preset name (unknown slugs read as `simple`,
	// exactly like ParseKind always did).
	public string KindName(string? kindSlug) =>
		IsDefinedKind(kindSlug) ? kindSlug! : WorkflowCatalog.ParseKind(kindSlug).ToString().ToLowerInvariant();

	// Builtin + defined kind slugs (for board-create error messages).
	public IEnumerable<string> KnownKinds() =>
		Enum.GetValues<BoardKind>().Select(k => k.ToString().ToLowerInvariant())
			.Concat(_kinds.Keys.OrderBy(k => k, StringComparer.Ordinal));

	// The workflow for (kind slug, type). Null = the kind needs a known type (same
	// "type required" contract as the catalog's Work kind).
	public Workflow? For(string? kindSlug, string? type)
	{
		if (kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind))
		{
			if (string.IsNullOrEmpty(type)) return null;
			var block = FindBlock(kind, type);
			return block is null ? null : ToWorkflow(block, type.ToLowerInvariant());
		}
		return WorkflowCatalog.For(WorkflowCatalog.ParseKind(kindSlug), type);
	}

	// All workflows hosted by a kind, one per type slug (status-filter validation).
	public IReadOnlyList<Workflow> Types(string? kindSlug) =>
		kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind)
			? kind.Workflows.SelectMany(b => b.Types.Select(t => ToWorkflow(b, t))).ToList()
			: WorkflowCatalog.Types(WorkflowCatalog.ParseKind(kindSlug));

	// Valid type slugs for a kind (for error messages).
	public string ValidTypes(string? kindSlug) =>
		kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind)
			? string.Join("|", kind.Workflows.SelectMany(b => b.Types))
			: WorkflowCatalog.ValidTypes(WorkflowCatalog.ParseKind(kindSlug));

	public bool QuickAddAllowed(string? kindSlug) =>
		kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind)
			? kind.QuickAddAllowed
			: WorkflowCatalog.QuickAddAllowed(WorkflowCatalog.ParseKind(kindSlug));

	// The type an untyped quick-add resolves to on a DEFINED kind: the first type of the
	// first block — declaration order is meaningful, like Statuses[0] = initial. Null for
	// catalog kinds (they keep their own per-kind defaults).
	public string? DefaultType(string? kindSlug) =>
		kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind)
			? kind.Workflows[0].Types[0]
			: null;

	// Project-wide status classification: the catalog scan FIRST (preset boards behave
	// exactly as before), then the defined kinds' vocabularies. Serves the cross-board
	// consumers (spec delivery roll-up, FSM effects, search indexability) where the node's
	// own board kind isn't in hand.
	public StatusKind? KindOfSlug(string slug) =>
		WorkflowCatalog.KindOfSlug(slug) ?? DefinedKindOfSlug(slug);

	public bool IsTerminalSlug(string slug) =>
		KindOfSlug(slug) is StatusKind.TerminalOk or StatusKind.TerminalCancel;

	// Per-board terminal classification (the closed-node predicate): a DEFINED kind
	// classifies against its own status vocabulary (falling back to the project-wide scan
	// only for an out-of-vocab legacy slug); a catalog kind keeps the preset-wide scan
	// exactly as today.
	public bool IsTerminalStatus(string? kindSlug, string statusSlug)
	{
		if (kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind))
			foreach (var block in kind.Workflows)
				if (StatusOf(block, statusSlug) is { } s)
					return s.Kind is StatusKind.TerminalOk or StatusKind.TerminalCancel;
		return IsTerminalSlug(statusSlug);
	}

	// All workflow BLOCKS of a kind (the tasks.workflow discovery shape). Defined kinds
	// group by declaration (one block per MethodologyWorkflowDef); catalog kinds collapse
	// identical FSMs (feature=bug=chore is one block), and Simple reports its whole type
	// vocabulary (type is a label there, not a workflow branch — the catalog's single entry
	// carries the placeholder type "simple", not what tasks.upsert accepts).
	public IReadOnlyList<WorkflowBlock> Blocks(string? kindSlug)
	{
		if (kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind))
			return kind.Workflows.Select(b => new WorkflowBlock(b.Types, ToWorkflow(b, b.Types[0]))).ToList();
		return CatalogBlocks(WorkflowCatalog.ParseKind(kindSlug));
	}

	static List<WorkflowBlock> CatalogBlocks(BoardKind kind)
	{
		var groups = new List<(List<string> Types, Workflow Wf)>();
		foreach (var w in WorkflowCatalog.Types(kind))
		{
			var types = kind == BoardKind.Simple ? WorkflowCatalog.SimpleTypes.ToList() : [w.Type];
			var match = groups.FindIndex(g => SameFsm(g.Wf, w));
			if (match < 0) groups.Add((types, w));
			else groups[match].Types.AddRange(types.Where(t => !groups[match].Types.Contains(t, StringComparer.OrdinalIgnoreCase)));
		}
		return groups.Select(g => new WorkflowBlock(g.Types, g.Wf)).ToList();
	}

	// Identical FSM = same statuses AND same transitions (record equality; Initial is
	// Statuses[0], so it's covered).
	static bool SameFsm(Workflow a, Workflow b) =>
		a.Statuses.SequenceEqual(b.Statuses) && a.Transitions.SequenceEqual(b.Transitions);

	StatusKind? DefinedKindOfSlug(string slug)
	{
		foreach (var kind in _kinds.Values)
			foreach (var block in kind.Workflows)
				if (StatusOf(block, slug) is { } s)
					return s.Kind;
		return null;
	}

	static WorkflowStatus? StatusOf(MethodologyWorkflowDef block, string slug) =>
		block.Statuses.FirstOrDefault(s => string.Equals(s.Slug, slug, StringComparison.OrdinalIgnoreCase));

	static MethodologyWorkflowDef? FindBlock(MethodologyKindDef kind, string type) =>
		kind.Workflows.FirstOrDefault(b => b.Types.Any(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase)));

	// Map a definition block onto the shared FSM vocabulary, carrying PreconditionArtifact.
	static Workflow ToWorkflow(MethodologyWorkflowDef block, string type) =>
		new(type, block.Statuses,
			block.Transitions.Select(t => new WorkflowTransition(t.From, t.To, t.RequiresApproval, t.RequiresReason, t.PreconditionArtifact)).ToList());
}
