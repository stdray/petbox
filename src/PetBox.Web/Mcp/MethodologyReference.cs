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
		[typeof(MethodologyWorkflowInput)] = "workflow",
		[typeof(MethodologyStatusInput)] = "status",
		[typeof(MethodologyTransitionInput)] = "transition",
		[typeof(MethodologyLinkConstraintInput)] = "linkConstraint",
		[typeof(MethodologyEffectInput)] = "effect",
		[typeof(MethodologyLinkKindInput)] = "linkKind",
		[typeof(MethodologyTagAxisInput)] = "tagAxis",
		[typeof(MethodologyMigrationInput)] = "migration entry",
		[typeof(MethodologyValueMapInput)] = "valueMap",
	};

	public static readonly IReadOnlyList<Entity> Entities =
	[
		Describe<MethodologyDefInput>(
			"The document root — the same shape tasks_methodology_def_get returns and tasks_methodology_def_upsert accepts (extra envelope fields like defined/version are ignored on save).",
			new()
			{
				["name"] = $"Methodology name, a {SlugSpec}.",
				["kinds"] = "The board kinds this methodology declares — at least one. A declared kind OVERRIDES the builtin preset of the same slug; every other kind keeps its preset.",
				["linkKinds"] = $"Additional project-declared relation kinds for relations_create (free semantic edges, no process meaning). Must not collide with the builtin kinds: {BuiltinRelationKinds}.",
				["tagAxes"] = "Declared tag namespaces. When present, every tag on a definition-resolved board must be <namespace>:value from this list; empty/omitted = free-form tags.",
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
			}),
		Describe<MethodologyTransitionInput>(
			"A directed FSM edge with its gates. Gates are transition DATA — the server enforces requiresApproval(enforceApproval)/requiresReason/preconditionArtifact; checklist stays a convention.",
			new()
			{
				["from"] = "Source status slug (of this block).",
				["to"] = "Target status slug (of this block).",
				["requiresApproval"] = "The approval gate: this move belongs to the owner/maintainer. By default a CONVENTION the guide states; see enforceApproval.",
				["requiresReason"] = "The move must carry a non-empty reason (e.g. triage → wontfix).",
				["preconditionArtifact"] = $"A comment-artifact tag ({SlugSpec}) the node must carry before the transition — an `artifact:<slug>` comment (e.g. \"spec_plan\" gates exploring → review). Enforced.",
				["enforceApproval"] = "Only with requiresApproval: true = the server BLOCKS the transition for a non-approver; false = owner-only by convention (the guide states it, the server does not block).",
				["checklist"] = "Free-text conditions to confirm before the transition. Rendered by the guide and marked on the graph; never server-enforced.",
			}),
		Describe<MethodologyLinkConstraintInput>(
			"\"A NEW node of `type` must carry a link of kind `link` at creation.\" Only upsert-expressible links can gate creation: task_spec (specRef) | blocks (blockedBy) | idea_spec (ideaRef).",
			new()
			{
				["type"] = "The type slug the constraint binds (must be declared by this kind's workflow blocks).",
				["link"] = "task_spec | blocks | idea_spec — the link kinds expressible in the create call itself.",
				["targetKind"] = "Optionally, the kind the linked node must be (e.g. a specRef must point at a spec node). Omitted = any kind.",
				["targetStatuses"] = "Optionally, statuses the linked node must be in (e.g. an ideaRef must point at an ACCEPTED idea). Omitted = any status.",
			}),
		Describe<MethodologyEffectInput>(
			"A kind-level transition effect: when a node of this kind ENTERS status `on`, linked nodes are moved — the data form of cross-board automation like \"work Done closes the intake issues that spawned it\".",
			new()
			{
				["on"] = "The trigger status (a status this kind's blocks declare).",
				["link"] = $"The relation kind traversed ({BuiltinRelationKinds}, or a declared linkKind).",
				["direction"] = "incoming | outgoing — whether the traversed edge points AT this node (incoming) or FROM it (outgoing).",
				["set"] = "The status linked nodes are set to (a status of the LINKED node's kind — resolved at runtime).",
				["onlyFrom"] = "Optionally restrict the effect to linked nodes currently in this status (e.g. only Blocked nodes unblock).",
			}),
		Describe<MethodologyLinkKindInput>(
			"A project-declared relation kind: a free semantic edge with no FSM effects, usable in relations_create.",
			new()
			{
				["slug"] = $"The relation kind's {SlugSpec}; must not collide with a builtin kind.",
				["description"] = "Optional human description (shown in the process guide's relation dictionary).",
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
