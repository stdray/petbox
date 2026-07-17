namespace PetBox.Tasks.Workflow;

// One workflow block of a board's FSM surface: every type slug sharing one state machine
// (the tasks_workflow shape — feature=bug=chore on a work board is ONE block).
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

	// Wrap an optionally-present definition — the null-coalesce every surface (service,
	// Razor pages) applies when it holds a MethodologyDefView? in hand.
	public static MethodologyRuntime From(MethodologyDefinition? definition) =>
		definition is null ? PresetsOnly : new(definition);

	// Builtin relation kinds with PROCESS meaning (FSM effects / guards key on these):
	// task_spec (specRef), issue_task (intake auto-close), idea_spec (ideaRef), blocks
	// (gating + unblock effect), part_of (decomposition), supersedes (obsoletion).
	public static readonly string[] ProcessRelationKinds = ["task_spec", "issue_task", "idea_spec", "blocks", "part_of", "supersedes"];

	// Builtin NEUTRAL kinds, available to EVERY project: free semantic edges between any
	// nodes — no FSM effects, no process meaning (spec primitives-link-kinds).
	public static readonly string[] NeutralRelationKinds = ["relates_to", "depends_on", "mirrors"];

	readonly Dictionary<string, MethodologyKindDef> _kinds;
	readonly IReadOnlyList<MethodologyKindDef> _declared;
	readonly IReadOnlyList<MethodologyLinkKindDef> _linkKinds;
	readonly IReadOnlyList<MethodologyTagAxisDef> _tagAxes;

	public MethodologyRuntime(MethodologyDefinition? definition)
	{
		_declared = definition?.Kinds ?? [];
		_kinds = _declared
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

	// Declared transition effects of a kind (schema v2), same merge as every resolver:
	// the definition's when it declares the kind, else the preset's (work: intake
	// auto-close + blocks auto-unblock — as data). Executed by RunTransitionEffectsAsync.
	public IReadOnlyList<MethodologyTransitionEffectDef> Effects(string? kindSlug) =>
		kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind)
			? kind.Effects ?? []
			: MethodologyPresets.KindDef(MethodologyPresets.ParseKind(kindSlug)).Effects;

	// Auto-wire target of a kind (primitives-enum-residual): the definition's when it
	// declares the kind, else the preset's (work → "spec"). Null = no auto-wire.
	public string? AutoWireSpecFrom(string? kindSlug) =>
		ResolvedKind(kindSlug)?.AutoWireSpecFrom;

	// Delivery roll-up config of a kind (primitives-enum-residual): the definition's when
	// it declares the kind, else the preset's (spec: required=feature, defect=bug). Null =
	// no delivery computation for this kind. Gates ComputeSpecDeliveryAsync.
	public MethodologyDeliveryDef? DeliveryOf(string? kindSlug) =>
		ResolvedKind(kindSlug)?.Delivery;

	// The methodology-declared default view mode for a kind (methodology-default-view-field):
	// merged PER FIELD, NOT per kind like ResolvedKind below (board-view-defaults-not-
	// applied-existing-instances) — a board provisioned from the quartet/classic BUILTIN
	// TEMPLATE materializes each preset MethodologyKindDef into the instance's stored
	// definition VERBATIM at creation time (RenderPresetDefinition); an instance created
	// before this field existed therefore stores DefaultView as null on a kind it otherwise
	// fully declares (e.g. `work`), and the whole-object merge in ResolvedKind reads that
	// null as "no default" instead of "no OPINION, use the preset" — the four `$system`
	// boards all opened in Tree in production because of exactly this. The definition's
	// DefaultView when non-null, else the preset's for the SAME resolved kind (work→kanban,
	// spec→outline, intake→table, ideas→tree; a wholly custom kind slug with no BoardKind
	// match falls back to Simple's DefaultView, itself null). Null overall = no methodology
	// preference — the caller (BoardViewModeRegistry.Resolve) falls through to the builtin
	// Tree default; never throws for a user-defined kind with no preset counterpart. The
	// name is format-only here; whether it is currently RENDERABLE is a PetBox.Web concern.
	public string? DefaultView(string? kindSlug) =>
		DeclaredField(kindSlug, k => k.DefaultView) ?? PresetField(kindSlug, k => k.DefaultView);

	// The outline view's reveal mode for a kind (board-view-mode-framework): the SAME per-
	// FIELD merge as DefaultView just above, for the identical reason (a pre-field-existing
	// materialized definition stores this null on a kind it otherwise fully declares). The
	// definition's OutlineReveal when non-null, else the preset's for the same resolved kind
	// (spec → inline-lazy; every other kind → null, read as Navigate below). Deliberately
	// NOT PresetKind, whose null-for-a-defined-kind guard is correct for process-role
	// behavior (FSM effects, delivery roll-up, quartet invariants) but wrong here: PresetKind
	// would read null for a perfectly ordinary `spec` board and the InlineLazy branch would
	// be unreachable in practice — exactly the bug this resolver exists to avoid.
	public string OutlineReveal(string? kindSlug) =>
		(DeclaredField(kindSlug, k => k.OutlineReveal) ?? PresetField(kindSlug, k => k.OutlineReveal))
			?? OutlineRevealModeNames.Navigate;

	// Process-role cardinality of a kind (spec methodology-kind-singleton): the SAME field-
	// level merge as DefaultView/OutlineReveal just above, not the whole-object ResolvedKind
	// merge every other process resolver uses — for the identical reason those two document:
	// a builtin-provisioned instance materializes each preset MethodologyKindDef VERBATIM at
	// creation time (RenderPresetDefinition), so an instance created before this field existed
	// stores Singleton as the JSON-missing null on a kind that IS a process role (e.g. `work`)
	// — a whole-object merge would read that as "not singleton" and silently drop the
	// invariant on every already-provisioned project. The declared kind's own bool? when set,
	// else the preset's for the SAME resolved kind, else false (a wholly custom kind with no
	// opinion is NOT singleton by default — opt-in, not a global law). Was gated on membership
	// in the `BoardKind` enum (MethodologyInstanceService.AssertProcessRoleSingletonAsync) —
	// a custom-declared kind could never opt in; now data, custom kinds included.
	public bool Singleton(string? kindSlug) =>
		(kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind) ? kind.Singleton : null)
			?? MethodologyPresets.KindDef(MethodologyPresets.ParseKind(kindSlug)).Singleton
			?? false;

	// The kind definition the process-role resolvers above (LinkConstraints, Effects,
	// AutoWireSpecFrom, DeliveryOf) share: definition override wins WHOLESALE when the kind
	// is declared, else the preset KindDef for the parsed BoardKind (unknown slugs → Simple).
	// Deliberately whole-object, unlike DefaultView/OutlineReveal above: a declared kind's
	// process fields (link constraints, effects, delivery roll-up, auto-wire target) are
	// process semantics the DEFINITION is the source of truth for — an omitted field there
	// means "this kind has none of that", not "inherit the preset's". Only the two display-
	// only view-mode fields get the field-level merge, because their documented null meaning
	// ("no opinion, use the builtin/preset default") is what a pre-field document actually
	// intends, and merging the other fields would silently reintroduce preset process
	// behavior (e.g. work's task_spec link constraint) onto a definition that deliberately
	// dropped it.
	MethodologyKindDef? ResolvedKind(string? kindSlug) =>
		kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind)
			? kind
			: MethodologyPresets.KindDef(MethodologyPresets.ParseKind(kindSlug));

	// The declared kind's own value for `select`, or null when the kind isn't declared at
	// all (the field-merge counterpart's "no opinion from the definition" half).
	string? DeclaredField(string? kindSlug, Func<MethodologyKindDef, string?> select) =>
		kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind) ? select(kind) : null;

	// The PRESET's value for `select`, resolved for the SAME kind slug (parsed to its
	// BoardKind, unknown → Simple) regardless of whether the definition declares it — the
	// field-merge counterpart's preset half, shared by DefaultView and OutlineReveal.
	static string? PresetField(string? kindSlug, Func<MethodologyKindDef, string?> select) =>
		select(MethodologyPresets.KindDef(MethodologyPresets.ParseKind(kindSlug)));

	// Tag axes governing a board of this kind — ONE rule for everything: no axes =
	// free-form tags, axes declared = enforced namespace allowlist. A definition-resolved
	// kind runs on the definition's axes (the definition is instance-scoped when the
	// board has MethodologyInstance membership; otherwise the project-singleton def —
	// transitional dual-read). A preset kind uses the preset's axes (quartet = builtin
	// area/concern, simple = none).
	public IReadOnlyList<MethodologyTagAxisDef> TagAxes(string? kindSlug) =>
		IsDefinedKind(kindSlug) ? _tagAxes : MethodologyPresets.TagAxes(MethodologyPresets.ParseKind(kindSlug));

	// Every relation kind this runtime accepts: builtin process + neutral kinds plus the
	// definition-declared ones (order = error-message order). Scope = the instance (or
	// legacy project singleton) that built this runtime.
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

	// Whether a board's EFFECTIVE kind is `spec` — works for a DEFINED kind too (production
	// regression, board-ui-review-findings #2, 2026-07): `PresetKind(...) == BoardKind.Spec`
	// reads null for any defined kind, and a project's `spec` board is virtually always
	// definition-resolved in practice — the quartet preset renders its kinds VERBATIM into a
	// methodology instance's stored definition at creation time (RenderPresetDefinition), so
	// `spec` becomes a "defined" kind slug there, not a bare preset one. `PresetKind(...) ==
	// BoardKind.Spec` therefore never matches on a real quartet-provisioned project — exactly
	// the trap `OutlineReveal`'s own comment above already warns against ("PresetKind would
	// read null for a perfectly ordinary `spec` board"). `KindName` already resolves correctly
	// for both shapes (a defined kind's own canonical slug, else the parsed preset's lowercase
	// name) — this just names the spec-board comparison once so callers don't each re-derive
	// (and re-break) it.
	public bool IsSpecKind(string? kindSlug) =>
		string.Equals(KindName(kindSlug), "spec", StringComparison.OrdinalIgnoreCase);

	// Whether a board's EFFECTIVE kind is `work` — the SAME effective-kind pattern as IsSpecKind
	// just above (presetkind-spec-blind-spot follow-up, found by that bug's sweep rather than
	// named in it): `work` is one of the quartet's four kinds, so RenderPresetDefinition renders
	// it VERBATIM into a real quartet-provisioned project's stored definition exactly like
	// `spec` — IsDefinedKind("work") is TRUE there, so `PresetKind(...) == BoardKind.Work` reads
	// null and never matches. The blocker guard (now GuardEngine.RequireBlockers) gated the "a Blocked task must
	// name a blocker" invariant on exactly that comparison, so it silently never fired on any
	// real project's work board (only on the bare-preset shape a hand-built test uses). Use this
	// wherever the question is "is this board's effective kind `work`", never PresetKind(...) ==
	// BoardKind.Work.
	public bool IsWorkKind(string? kindSlug) =>
		string.Equals(KindName(kindSlug), "work", StringComparison.OrdinalIgnoreCase);

	// The preset kinds in guide order: the quartet pipeline first, then the standalone
	// kinds (`classic`, `simple` last).
	static readonly BoardKind[] PipelineOrder = [BoardKind.Intake, BoardKind.Ideas, BoardKind.Spec, BoardKind.Work, BoardKind.Classic, BoardKind.Simple];

	// The project's EFFECTIVE kind set — what the process guide renders: every definition-
	// declared kind (declaration order) followed by every preset kind the definition does
	// NOT override (pipeline order intake→ideas→spec→work, then simple). This is the same
	// per-kind merge every resolver here applies, materialized as one list of definitions.
	public IReadOnlyList<MethodologyKindDef> EffectiveKinds() =>
		_declared
			.Concat(PipelineOrder
				.Where(k => !_kinds.ContainsKey(k.ToString().ToLowerInvariant()))
				.Select(MethodologyPresets.KindDef))
			.ToList();

	// Definition-declared relation kinds with their descriptions (the guide's dictionary
	// section; KnownRelationKinds flattens these to slugs). Instance-scoped when the
	// runtime was built from an instance rules document.
	public IReadOnlyList<MethodologyLinkKindDef> DeclaredLinkKinds => _linkKinds;

	// Builtin + defined kind slugs (for board-create error messages).
	public IEnumerable<string> KnownKinds() =>
		Enum.GetValues<BoardKind>().Select(k => k.ToString().ToLowerInvariant())
			.Concat(_kinds.Keys.OrderBy(k => k, StringComparer.Ordinal));

	// The workflow for (kind slug, type). Null = the kind needs a known type (the "type
	// required" contract). Instance rules store full preset blobs as defined kinds, so
	// empty-type resolution MUST match MethodologyPresets.For: Work requires an explicit
	// type; other kinds resolve the first block's default type FSM.
	public Workflow? For(string? kindSlug, string? type)
	{
		if (kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind))
		{
			if (string.IsNullOrEmpty(type))
			{
				if (string.Equals(kindSlug, "work", StringComparison.OrdinalIgnoreCase))
					return null;
				if (kind.Workflows.Count == 0 || kind.Workflows[0].Types.Count == 0)
					return null;
				var defType = kind.Workflows[0].Types[0];
				return kind.Workflows[0].ToWorkflow(defType);
			}
			var label = type.ToLowerInvariant();
			var block = FindBlock(kind, label);
			return block?.ToWorkflow(label);
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

	// Per-board status classification (badge coloring and the closed-node predicate): a
	// DEFINED kind classifies against its own status vocabulary (falling back to the
	// project-wide scan only for an out-of-vocab legacy slug); a preset kind keeps the
	// preset-wide scan exactly as today.
	public StatusKind? StatusKindOf(string? kindSlug, string statusSlug)
	{
		if (kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind))
			foreach (var block in kind.Workflows)
				if (StatusOf(block, statusSlug) is { } s)
					return s.Kind;
		return KindOfSlug(statusSlug);
	}

	// Per-board terminal classification — StatusKindOf's terminal projection.
	public bool IsTerminalStatus(string? kindSlug, string statusSlug) =>
		StatusKindOf(kindSlug, statusSlug) is StatusKind.TerminalOk or StatusKind.TerminalCancel;

	// The human display Name for a status on a board — the ONE place the UI turns a stored slug
	// into a label (status badge + status-change select), so PascalCase work statuses and
	// lowercase methodology statuses read consistently ("In progress", "Defined"). A DEFINED kind
	// uses its own declared status Name; a preset kind uses the preset Name; an out-of-vocab legacy
	// slug falls back to the slug verbatim. Presentation only — storage/transitions use the slug.
	public string StatusName(string? kindSlug, string statusSlug)
	{
		if (kindSlug is not null && _kinds.TryGetValue(kindSlug, out var kind))
			foreach (var block in kind.Workflows)
				if (StatusOf(block, statusSlug) is { } s)
					return s.Name;
		return MethodologyPresets.NameOfSlug(statusSlug) ?? statusSlug;
	}

	// All workflow BLOCKS of a kind (the tasks_workflow discovery shape). One block per
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
