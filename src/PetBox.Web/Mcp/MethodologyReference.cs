using System.Reflection;
using System.Text.Json;
using PetBox.Tasks.Workflow;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// The "Definition reference" the methodology editor renders: every entity and field of the
// definition document, in the exact wire vocabulary ParseDocument accepts. The STRUCTURE is
// AUTO-GENERATED — entities, field names and field types are reflected off the SAME wire
// DTOs the parser deserializes (MethodologyDefInput & co.), so adding/renaming a field in
// the wire surfaces here by construction and cannot silently drift. Only the field
// DESCRIPTIONS are hand-written (colocated below); a field whose description is missing
// renders a visible "(undocumented — describe it in MethodologyReference)" rather than
// disappearing. Vocabularies (status kinds, effect directions, builtin relation kinds) are
// pulled from the domain enums/constants where they exist.
static class MethodologyReference
{
	public sealed record Field(string Name, string Type, string Description);

	public sealed record Entity(string Name, string Summary, IReadOnlyList<Field> Fields);

	const string SlugSpec = "slug (^[a-z][a-z0-9_-]{0,99}$)";

	// open|terminalok|terminalcancel — derived from the StatusKind enum, not restated.
	static readonly string StatusKinds =
		string.Join("|", Enum.GetNames<StatusKind>().Select(n => n.ToLowerInvariant()));

	static readonly string BuiltinRelationKinds =
		string.Join(", ", MethodologyRuntime.ProcessRelationKinds.Concat(MethodologyRuntime.NeutralRelationKinds));

	// Wire-DTO element type → the reference entity name it renders as (also the cross-link
	// label used when a field's type is an array of that entity).
	static readonly Dictionary<Type, string> EntityNames = new()
	{
		[typeof(MethodologyDefInput)] = "definition",
		[typeof(MethodologyKindInput)] = "kind",
		[typeof(MethodologyDeliveryInput)] = "delivery",
		[typeof(MethodologyBlocksGateInput)] = "blocksGate",
		[typeof(MethodologyWorkflowInput)] = "workflow",
		[typeof(MethodologyStatusInput)] = "status",
		[typeof(MethodologyTransitionInput)] = "transition",
		[typeof(MethodologyLinkConstraintInput)] = "linkConstraint",
		[typeof(MethodologyEffectInput)] = "effect",
		[typeof(MethodologyLinkKindInput)] = "linkKind",
		[typeof(MethodologyTagAxisInput)] = "tagAxis",
		[typeof(MethodologyMigrationInput)] = "migration entry",
		[typeof(MethodologyValueMapInput)] = "valueMap",
		[typeof(MethodologyRequiredArtifactInput)] = "requiredArtifact",
		[typeof(MethodologyGateEnforcementInput)] = "gateEnforcement",
		[typeof(MethodologyLinkDirectionInput)] = "linkDirection",
	};

	public static readonly IReadOnlyList<Entity> Entities =
	[
		Describe<MethodologyDefInput>(
			"The document root — the same shape tasks_methodology_template_get / tasks_methodology_rules_get return and template_upsert / rules_upsert accept (extra envelope fields like found/version are ignored on save).",
			new()
			{
				["name"] = $"Methodology name, a {SlugSpec}.",
				["kinds"] = "The board kinds this methodology declares — at least one. A declared kind OVERRIDES the builtin preset of the same slug; every other kind keeps its preset.",
				["linkKinds"] = $"Additional project-declared relation kinds for relations_create (free semantic edges, no process meaning). Must not collide with the builtin kinds: {BuiltinRelationKinds}.",
				["tagAxes"] = "Declared tag namespaces. When present, every tag on a definition-resolved board must be <namespace>:value from this list; empty/omitted = free-form tags.",
				["strictMode"] = "The definition-wide default for a transition's approval enforcement (spec methodology-gate-strictness): a transition whose own `enforce.approval` is unset falls back to this. Default false — owner-only stays a CONVENTION, reproducing today's behavior. Never toughens a builtin preset kind this definition doesn't declare.",
			}),
		Describe<MethodologyKindInput>(
			"One board kind. `kind` is a FREE-FORM slug — user-defined kinds are the point, not limited to the builtin intake|ideas|spec|work|simple|classic.",
			new()
			{
				["kind"] = $"The kind's {SlugSpec}. Boards are created with this kind via tasks_board_create.",
				["quickAddAllowed"] = "Whether the bare board quick-add form may create nodes of this kind (default true; turn off when a node needs a link at birth, like the spec/work presets).",
				["workflows"] = "The kind's workflow blocks — at least one. A type slug maps to exactly one block across the kind.",
				["linkConstraints"] = "Per-type creation link requirements (\"a NEW <type> must carry a <link>\"). Omitted = no requirement; constraints are opt-in per type.",
				["effects"] = "Declared transition effects — cross-node automation the server executes when a node of this kind enters the trigger status.",
				["autoWireSpecFrom"] = "Auto-wire this kind's boards to the sole active board of the named kind (e.g. work → \"spec\"), so specRef resolves without naming a board. Omitted = no auto-wire.",
				["delivery"] = "The bottom-up delivery roll-up for this kind (a spec node's verdict computed from the work nodes linked to it). Omitted = this kind computes no delivery.",
				["defaultView"] = "The board's initial view mode when a viewer has no saved preference. Omitted = the builtin default.",
				["outlineReveal"] = "How the outline reveals descendants of a node. Omitted = the builtin default.",
				["singleton"] = "true = at most one open board of this kind per methodology instance (the quartet's work/spec/ideas/intake are singleton; classic/simple are not). Omitted = falls back to the builtin preset of the same slug, else not singleton.",
				["blocksGate"] = "The blocking-gate statuses: a node in `status` must name a blocker (a STATE invariant checked on every write, not a transition gate); a released node moves to `releaseTo`. Omitted = this kind has no blocking gate (falls back to the builtin preset of the same slug, else none) — only work is gated today, but a definition can opt any kind in.",
				["description"] = "Optional free-form prose about this kind (data, not code). Surfaced by the compiled process guide (tasks_methodology_guide); never resolved or enforced. Edit it alone with tasks_methodology_describe instead of a whole-document rules_upsert.",
				["boardName"] = "The preferred board name for this kind, tried FIRST when a board of this kind is provisioned (still subject to the usual name-collision/reserved-name rules). Omitted = no opinion — the board is named from the kind slug as before.",
			}),
		Describe<MethodologyBlocksGateInput>(
			"The blocking-gate statuses of a kind (spec methodology-blocks-gate-data).",
			new()
			{
				["status"] = "The status a node of this kind must name a blocker to enter or hold (e.g. \"Blocked\").",
				["releaseTo"] = "The status a released node moves to when its last blocker resolves (e.g. \"InProgress\").",
			}),
		Describe<MethodologyDeliveryInput>(
			"The delivery roll-up definition: how a node of the OWNING kind derives its verdict (not_started | in_progress | done | done_with_defects) from the nodes linked to it. Omit the whole object and the kind computes no delivery at all — the roll-up is gated by this DATA, so an absent definition silently disables the feature (work/delivery-rollup-is-vacuous-in-prod).",
			new()
			{
				["requiredTypes"] = "The type slugs that DRIVE progress (e.g. [\"feature\"]): none linked → not_started; any not in a terminalok status → in_progress; all terminalok → a done candidate.",
				["defectTypes"] = "The type slugs counted as defects (e.g. [\"bug\"]): once the requireds are done, any defect still in an OPEN status turns the verdict into done_with_defects. Omitted/empty = done has no defect variant.",
			}),
		Describe<MethodologyWorkflowInput>(
			"One state machine shared by every type slug in `types`. Convention: statuses[0] is the initial status.",
			new()
			{
				["types"] = $"Type slugs sharing this state machine ({SlugSpec} each) — at least one; unique across the kind's blocks.",
				["statuses"] = "The block's status vocabulary — at least one; slugs unique per block (case-insensitive). The FIRST status is the initial one.",
				["transitions"] = "The directed edges of the state machine. Only listed moves are allowed; both ends must be statuses of THIS block.",
			}),
		Describe<MethodologyStatusInput>(
			"A workflow status.",
			new()
			{
				["slug"] = "The stored status value (case-insensitive matching; PascalCase like \"InProgress\" is fine — status slugs are exempt from the lowercase slug spec).",
				["name"] = "Human display name (defaults to the slug).",
				["kind"] = $"{StatusKinds} (default open). Terminal statuses close the node (hidden from active listings); terminalok = delivered, terminalcancel = abandoned.",
				["description"] = "Optional free-form prose about this status. Surfaced by the compiled process guide; never resolved or enforced. Edit it alone with tasks_methodology_describe.",
			}),
		Describe<MethodologyTransitionInput>(
			"A directed FSM edge with its gates. Gates are transition DATA — the server enforces requiresApproval(enforceApproval)/requiresReason/preconditionArtifact; checklist stays a convention. `requiredArtifacts`/`enforce` are the schema-v2 replacement for requiresReason/preconditionArtifact/enforceApproval (spec methodology-gate-strictness) — don't declare both shapes on one transition.",
			new()
			{
				["from"] = "Source status slug (of this block).",
				["to"] = "Target status slug (of this block).",
				["requiresApproval"] = "The approval gate: this move belongs to the owner/maintainer. By default a CONVENTION the guide states; see enforceApproval.",
				["requiresReason"] = "LEGACY — the move must carry a non-empty reason (e.g. triage → wontfix). Prefer requiredArtifacts:[{slug:\"reason\",inline:true}] in new documents; don't set both.",
				["preconditionArtifact"] = $"LEGACY — a comment-artifact tag ({SlugSpec}) the node must carry before the transition — an `artifact:<slug>` comment (e.g. \"spec_plan\" gates exploring → review). Enforced. Prefer requiredArtifacts:[{{slug}}] in new documents; don't set both.",
				["enforceApproval"] = "LEGACY — only with requiresApproval: true = the server BLOCKS the transition for a non-approver; false = owner-only by convention (the guide states it, the server does not block). Prefer enforce.approval in new documents.",
				["checklist"] = "Free-text conditions to confirm before the transition. Rendered by the guide and marked on the graph; never server-enforced.",
				["description"] = "Optional free-form prose about this transition. Surfaced by the compiled process guide; never resolved or enforced. Edit it alone with tasks_methodology_describe.",
				["requiredArtifacts"] = "The unified artifact gate (schema v2, spec methodology-gate-strictness): every comment artifact this transition needs. `reason` is just an artifact with slug \"reason\", inline:true — there is no separate reason gate. Empty/omitted = declare via the legacy fields instead (or no artifact gate).",
				["enforce"] = "This transition's strictness override: which of its declared gates the server actually blocks on. Omitted = fall through to the defaults (approval → the definition's strictMode; artifacts → always hard, reproducing today's behavior).",
			}),
		Describe<MethodologyRequiredArtifactInput>(
			"One entry of a transition's requiredArtifacts gate (schema v2, spec methodology-gate-strictness) — the union of the legacy requiresReason/preconditionArtifact pair.",
			new()
			{
				["slug"] = $"The artifact tag ({SlugSpec}) — an `artifact:<slug>` comment on the node. \"reason\" is the one legal inline slug (see inline).",
				["inline"] = "true = the content rides THIS SAME call (today only slug \"reason\", via the `reason` field on tasks_upsert — v1 has no other inline channel, so any other slug with inline:true is rejected). false (default) = a pre-existing `artifact:<slug>` comment must already be on the node.",
			}),
		Describe<MethodologyGateEnforcementInput>(
			"What the server actually BLOCKS for one transition's gates (schema v2, spec methodology-gate-strictness) — the FORCE half, separate from the DECLARATION (requiresApproval/requiredArtifacts). Both fields independently nullable: omitted = no opinion, fall through to that field's own default.",
			new()
			{
				["approval"] = "Omitted = fall back to the definition's strictMode (default false — owner-only stays a convention). true/false explicitly overrides it for THIS transition.",
				["artifacts"] = "Omitted = true (reason/precondition artifacts are hard today, unconditionally — this reproduces that). false demotes the gate to a convention the guide states but the server does not block.",
			}),
		Describe<MethodologyLinkConstraintInput>(
			"\"A NEW node of `type` must carry a link of kind `link` at creation.\" Only upsert-expressible links can gate creation: task_spec (specRef) | blocks (blockedBy) | idea_spec (ideaRef).",
			new()
			{
				["type"] = "The type slug the constraint binds (must be declared by this kind's workflow blocks).",
				["link"] = "task_spec | blocks | idea_spec — the link kinds expressible in the create call itself.",
				["targetKind"] = "Optionally, the kind the linked node must be (e.g. a specRef must point at a spec node). Omitted = any kind.",
				["targetStatuses"] = "Optionally, statuses the linked node must be in (e.g. an ideaRef must point at an ACCEPTED idea). Omitted = any status.",
				["description"] = "Optional free-form prose about why this constraint exists. Surfaced by the compiled process guide; never resolved or enforced. Edit it alone with tasks_methodology_describe.",
			}),
		Describe<MethodologyEffectInput>(
			"A kind-level transition effect: when a node of this kind ENTERS status `on` (default) or LEAVES it (`onLeave`, Effect.onLeave), linked nodes are moved — the data form of cross-board automation like \"work Done closes the intake issues that spawned it\", or \"leaving Blocked closes the incoming blocks edges\".",
			new()
			{
				["on"] = "The trigger status (a status this kind's blocks declare).",
				["link"] = $"The relation kind traversed ({BuiltinRelationKinds}, or a declared linkKind).",
				["direction"] = "incoming | outgoing — whether the traversed edge points AT this node (incoming) or FROM it (outgoing).",
				["set"] = "The status linked nodes are set to (a status of the LINKED node's kind — resolved at runtime). Omitted = a pure edge-consumption effect (no status propagated).",
				["onlyFrom"] = "Optionally restrict the effect to linked nodes currently in this status (e.g. only Blocked nodes unblock).",
				["onLeave"] = "true = fire when a node of this kind LEAVES `on` instead of entering it (Effect.onLeave). Default false (enter).",
				["description"] = "Optional free-form prose about why this effect exists. Surfaced by the compiled process guide; never resolved or enforced. Edit it alone with tasks_methodology_describe.",
			}),
		Describe<MethodologyLinkKindInput>(
			"A project-declared relation kind: a free semantic edge with no FSM effects, usable in relations_create.",
			new()
			{
				["slug"] = $"The relation kind's {SlugSpec}; must not collide with a builtin kind.",
				["description"] = "Optional human description (shown in the process guide's relation dictionary).",
				["category"] = "neutral | process (default neutral). Declaration only in v1 — process marks the edge as process-bearing for the guide; it does not yet change effect/guard behavior.",
				["direction"] = "Optional stored-edge orientation (fromKind/toKind name the node kinds at each end of relations.from→to). Omitted = the edge is unoriented.",
			}),
		Describe<MethodologyLinkDirectionInput>(
			"The STORED orientation of a declared relation kind's edge (spec methodology-link-kinds-declared). fromKind/toKind constrain the node KIND at each end of the stored edge (relations.from→to), NOT a semantic reading — the human reading goes in label.",
			new()
			{
				["fromKind"] = "The kind of the node at the FROM end of the stored edge (must be a kind this definition declares). Omitted = that end is unconstrained.",
				["toKind"] = "The kind of the node at the TO end of the stored edge (must be a kind this definition declares). Omitted = that end is unconstrained.",
				["label"] = "The human reading of the edge (e.g. \"delivers to\"), shown in the process guide's relation dictionary.",
			}),
		Describe<MethodologyTagAxisInput>(
			"A declared tag namespace: with axes declared, every tag on a definition-resolved board must be <namespace>:value.",
			new()
			{
				["namespace"] = $"The axis {SlugSpec} (e.g. \"area\", \"concern\").",
				["description"] = "Optional human description.",
			}),
		Describe<MethodologyMigrationInput>(
			"One entry of the OPTIONAL migration document (the second textarea): per-kind {from,to} repairs for live nodes an incompatible definition change would strand. A mapping applies ONLY where a node's current value is invalid under the new resolution.",
			new()
			{
				["kind"] = "The board-kind slug the mappings repair (a kind of the new definition, a builtin kind, or the slug of a kind the new definition dropped).",
				["types"] = "Type repairs: {from,to} pairs; `to` must be a type of the new resolution.",
				["statuses"] = "Status repairs: {from,to} pairs; `to` must be a status of the new resolution.",
			}),
		Describe<MethodologyValueMapInput>(
			"One {from,to} value repair.",
			new()
			{
				["from"] = "The stranded value a live node currently carries.",
				["to"] = "The value it is rewritten to (must be valid under the new definition).",
			}),
	];

	// Reflect one wire DTO into a reference entity: JSON field names via the camelCase
	// policy (the wire's own naming), field types humanized, descriptions looked up by the
	// JSON name — an undescribed field renders a VISIBLE placeholder, never disappears.
	static Entity Describe<T>(string summary, Dictionary<string, string> descriptions)
	{
		var fields = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Select(p =>
			{
				var name = JsonNamingPolicy.CamelCase.ConvertName(p.Name);
				return new Field(
					name,
					TypeLabel(p.PropertyType),
					descriptions.TryGetValue(name, out var d) ? d : "(undocumented — describe it in MethodologyReference)");
			})
			.ToList();
		return new Entity(EntityNames[typeof(T)], summary, fields);
	}

	static string TypeLabel(Type t)
	{
		var inner = Nullable.GetUnderlyingType(t) ?? t;
		if (inner.IsArray)
			return $"array of {TypeLabel(inner.GetElementType()!)}";
		if (EntityNames.TryGetValue(inner, out var entity))
			return entity;
		if (inner == typeof(string)) return "string";
		if (inner == typeof(bool)) return "bool";
		if (inner == typeof(long) || inner == typeof(int)) return "number";
		return inner.Name;
	}
}
