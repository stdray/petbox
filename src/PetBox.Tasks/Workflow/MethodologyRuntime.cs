namespace PetBox.Tasks.Workflow;

// One workflow block of a board's FSM surface: every type slug sharing one state machine
// (the tasks.workflow shape — feature=bug=chore on a work board is ONE block).
public sealed record WorkflowBlock(IReadOnlyList<string> Types, Workflow Workflow);

// The data-driven FSM resolution seam (engine wave 1.2): given a project's optional
// MethodologyDefinition, answers every workflow question per KIND SLUG (string), because
// user-defined kinds don't live on the BoardKind enum. MERGE semantics: a definition
// OVERRIDES per kind — a kind slug the definition declares resolves from its data; every
// other kind slug falls back to the built-in PRESET DEFINITIONS (MethodologyPresets), the
// same definition shapes constructed in code — so a project can define one custom kind
// and keep the quartet. No definition → pure presets. The instance is immutable and
// cheap: the service loads the definition once per call and resolves through this BEFORE
// building queries (the helpers are sync).
public sealed class MethodologyRuntime
{
	// The no-definition runtime: every answer falls through to the built-in presets.
	public static readonly MethodologyRuntime PresetsOnly = new(null);

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

	// Creation link constraints of a kind: the definition's when it declares the kind,
	// else the preset's (work: feature/bug need task_spec; chore free — as data).
	public IReadOnlyList<MethodologyLinkConstraintDef> LinkConstraints(string? kindSlug) =>
		kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind)
			? kind.LinkConstraints ?? []
			: MethodologyPresets.KindDef(MethodologyPresets.ParseKind(kindSlug)).LinkConstraints;

	// Tag axes governing a board of this kind — ONE rule for everything: no axes =
	// free-form tags, axes declared = enforced namespace allowlist. A definition-resolved
	// kind runs on the definition's (project-wide) axes; a preset kind on the preset's
	// (quartet = builtin area/concern, simple = none).
	public IReadOnlyList<MethodologyTagAxisDef> TagAxes(string? kindSlug) =>
		IsDefinedKind(kindSlug) ? _tagAxes : MethodologyPresets.TagAxes(MethodologyPresets.ParseKind(kindSlug));

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

	// The PROCESS-ROLE enum serving this kind, or null when the definition overrides it —
	// the guard for preset-only behavior (quartet invariants, spec delivery, FSM effects):
	// a definition-declared custom kind has no process role.
	public BoardKind? PresetKind(string? kindSlug) =>
		IsDefinedKind(kindSlug) ? null : MethodologyPresets.ParseKind(kindSlug);

	// The kind name a view/response reports: a defined kind verbatim (definition slugs are
	// canonical lowercase), else the parsed preset name (unknown slugs read as `simple`,
	// exactly like ParseKind always did).
	public string KindName(string? kindSlug) =>
		IsDefinedKind(kindSlug) ? kindSlug! : MethodologyPresets.ParseKind(kindSlug).ToString().ToLowerInvariant();

	// Builtin + defined kind slugs (for board-create error messages).
	public IEnumerable<string> KnownKinds() =>
		Enum.GetValues<BoardKind>().Select(k => k.ToString().ToLowerInvariant())
			.Concat(_kinds.Keys.OrderBy(k => k, StringComparer.Ordinal));

	// The workflow for (kind slug, type). Null = the kind needs a known type (the "type
	// required" contract — a defined kind always, a preset kind only for Work).
	public Workflow? For(string? kindSlug, string? type)
	{
		if (kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind))
		{
			if (string.IsNullOrEmpty(type)) return null;
			var block = FindBlock(kind, type);
			return block?.ToWorkflow(type.ToLowerInvariant());
		}
		return MethodologyPresets.For(MethodologyPresets.ParseKind(kindSlug), type);
	}

	// All workflows hosted by a kind, one per type slug (status-filter validation).
	public IReadOnlyList<Workflow> Types(string? kindSlug) =>
		kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind)
			? kind.Workflows.SelectMany(b => b.Types.Select(b.ToWorkflow)).ToList()
			: MethodologyPresets.Types(MethodologyPresets.ParseKind(kindSlug));

	// Valid type slugs for a kind (for error messages).
	public string ValidTypes(string? kindSlug) =>
		kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind)
			? string.Join("|", kind.Workflows.SelectMany(b => b.Types))
			: MethodologyPresets.ValidTypes(MethodologyPresets.ParseKind(kindSlug));

	public bool QuickAddAllowed(string? kindSlug) =>
		kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind)
			? kind.QuickAddAllowed
			: MethodologyPresets.QuickAddAllowed(MethodologyPresets.ParseKind(kindSlug));

	// The type an untyped quick-add resolves to: the first type of the first block —
	// declaration order is meaningful, like Statuses[0] = initial. One convention for
	// defined AND preset kinds (ideas→idea, spec→spec, intake→issue, simple→task).
	public string DefaultType(string? kindSlug) =>
		kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind)
			? kind.Workflows[0].Types[0]
			: MethodologyPresets.DefaultType(MethodologyPresets.ParseKind(kindSlug));

	// Project-wide status classification: the preset scan FIRST (preset boards behave
	// exactly as before), then the defined kinds' vocabularies. Serves the cross-board
	// consumers (spec delivery roll-up, FSM effects, search indexability) where the node's
	// own board kind isn't in hand.
	public StatusKind? KindOfSlug(string slug) =>
		MethodologyPresets.KindOfSlug(slug) ?? DefinedKindOfSlug(slug);

	public bool IsTerminalSlug(string slug) =>
		KindOfSlug(slug) is StatusKind.TerminalOk or StatusKind.TerminalCancel;

	// Per-board terminal classification (the closed-node predicate): a DEFINED kind
	// classifies against its own status vocabulary (falling back to the project-wide scan
	// only for an out-of-vocab legacy slug); a preset kind keeps the preset-wide scan
	// exactly as today.
	public bool IsTerminalStatus(string? kindSlug, string statusSlug)
	{
		if (kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind))
			foreach (var block in kind.Workflows)
				if (StatusOf(block, statusSlug) is { } s)
					return s.Kind is StatusKind.TerminalOk or StatusKind.TerminalCancel;
		return IsTerminalSlug(statusSlug);
	}

	// All workflow BLOCKS of a kind (the tasks.workflow discovery shape). One block per
	// workflow declaration, for defined and preset kinds alike — the preset data is
	// already grouped by shared FSM (feature=bug=chore is one block, and Simple's block
	// carries its whole type vocabulary).
	public IReadOnlyList<WorkflowBlock> Blocks(string? kindSlug) =>
		kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind)
			? kind.Workflows.Select(b => new WorkflowBlock(b.Types, b.ToWorkflow(b.Types[0]))).ToList()
			: MethodologyPresets.Blocks(MethodologyPresets.ParseKind(kindSlug));

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
}
