namespace PetBox.Web.Mcp.Contract;

// Typed MCP tool-INPUT records (typed-surface Phase 4 — typed inputs). The structural
// array params of tasks_upsert (`nodes`) and memory_upsert (`entries`) used to arrive as a
// raw JsonElement, so the SDK-generated inputSchema was an opaque blob and the per-field
// types were invisible to the client (the class of bug that produced the config_binding
// stringified-object trap). Making the param a typed array (PlanNodeInput[] / MemoryEntryInputDto[])
// makes the SDK emit a real per-field array schema.
//
// STALE-SCHEMA TRADE-OFF: the old JsonElement parser also accepted the array double-encoded as
// a JSON *string* ("[{...}]") — the MCP stale-schema gotcha (a client whose cached schema still
// believes the param is a string). With a typed-array parameter the SDK deserializes the
// argument into PlanNodeInput[] BEFORE our code runs, so a string payload now fails to bind at
// the SDK layer (a custom tolerant converter would flatten the element schema back to opaque,
// defeating the whole point). This is an accepted deviation: the typed schema is the primary
// goal, and the stale-string risk is bounded to within a single MCP session (a reconnect
// refreshes the cached schema and fixes it). See the report.

// A node as submitted to tasks_upsert. Mirrors EXACTLY the fields the old JsonElement parser
// (TasksTools.ParseNodePatches/ParseTags/ResolveKey/ResolvePrevKey) accepted. Field semantics
// (null = omit/inherit, "" = explicit clear) are unchanged — they are now carried by the JSON
// value itself (an omitted property deserializes to null = inherit; an explicit "" stays "").
public sealed record PlanNodeInput
{
	// Flat board-unique slug. `l1` is accepted as a back-compat alias on the wire (see converter).
	public string? Key { get; init; }
	public string? L1 { get; init; }

	// Rename source slug (PrevKey), `prevL1` accepted as the back-compat alias.
	public string? PrevKey { get; init; }
	public string? PrevL1 { get; init; }

	// Vertical decomposition: parent slug | NodeId. null = omit, "" = detach to root.
	public string? PartOf { get; init; }

	// Per-node links. specRef → spec NodeId implemented; ideaRef → accepted idea (spec boards);
	// blockedBy → blocking NodeId; supersedes → slug|NodeId this node replaces.
	public string? SpecRef { get; init; }
	public string? IdeaRef { get; init; }
	public string? BlockedBy { get; init; }
	public string? Supersedes { get; init; }

	public string? Status { get; init; }
	public string? Type { get; init; }
	public string? Title { get; init; }
	public string? Body { get; init; }
	public string? CommitRef { get; init; }

	// Baseline version last seen (0 = new); sparse ordering int.
	public long Version { get; init; }
	public long? Priority { get; init; }

	// Enforced tags ("namespace:value", namespaces area|concern). null = omit; a non-null list
	// (incl. empty) REPLACES the node's full tag set. A CSV string is also tolerated on the wire.
	public IReadOnlyList<string>? Tags { get; init; }

	// Soft-delete marker: { key, deleted:true } temporal-closes the active node (optional
	// version baseline). Mirrors memory_upsert's marker; other fields are ignored.
	public bool Deleted { get; init; }
}

// The `definition` argument of tasks_methodology_def_upsert — the whole user-defined
// methodology as a structured document (typed records, not a JSON string/blob, per the
// typed-surface convention). Mirrors MethodologyDefinition; the adapter maps it 1:1 and
// the service validates integrity (slugs, per-block references, uniqueness).
public sealed record MethodologyDefInput
{
	// Methodology name, a slug ([a-z][a-z0-9_-]{0,99}).
	public string? Name { get; init; }
	public MethodologyKindInput[]? Kinds { get; init; }
	// Project-declared relation kinds (effect-free), usable in relations_create alongside
	// the builtin process + neutral kinds.
	public MethodologyLinkKindInput[]? LinkKinds { get; init; }
	// Declared tag namespaces: when present, tags on definition-resolved boards must be
	// `<namespace>:value` with the namespace from this list; empty/omitted = free-form.
	public MethodologyTagAxisInput[]? TagAxes { get; init; }
}

// One board kind of the methodology. `kind` is a FREE-FORM slug (user-defined kinds are
// the point — not limited to the built-in simple|spec|ideas|intake|work).
public sealed record MethodologyKindInput
{
	public string? Kind { get; init; }
	// Whether the bare board quick-add form may create nodes of this kind (default true;
	// the built-in preset turns it off where a node needs a link at birth: spec/work).
	public bool QuickAddAllowed { get; init; } = true;
	public MethodologyWorkflowInput[]? Workflows { get; init; }
	// Per-type creation link requirements ("a new <type> must carry a <link>"); omitted =
	// no requirement (constraints are opt-in per type).
	public MethodologyLinkConstraintInput[]? LinkConstraints { get; init; }
}

// "A NEW node of type `type` must carry a link of kind `link` at creation." `link` must
// be upsert-expressible: task_spec (specRef) | blocks (blockedBy) | idea_spec (ideaRef).
public sealed record MethodologyLinkConstraintInput
{
	public string? Type { get; init; }
	public string? Link { get; init; }
}

// A project-declared relation kind: a free semantic edge, no FSM effects. `slug` must not
// collide with a builtin process/neutral kind.
public sealed record MethodologyLinkKindInput
{
	public string? Slug { get; init; }
	public string? Description { get; init; }
}

// A declared tag namespace (axis) for the project's definition-resolved boards.
public sealed record MethodologyTagAxisInput
{
	public string? Namespace { get; init; }
	public string? Description { get; init; }
}

// One state machine shared by every type slug in `types` (the tasks_workflow block
// shape). Convention: statuses[0] is the initial status.
public sealed record MethodologyWorkflowInput
{
	public string[]? Types { get; init; }
	public MethodologyStatusInput[]? Statuses { get; init; }
	public MethodologyTransitionInput[]? Transitions { get; init; }
}

// A workflow status: `kind` is open|terminalok|terminalcancel (default open), the same
// string vocabulary tasks_workflow answers with; `name` defaults to the slug.
public sealed record MethodologyStatusInput
{
	public string? Slug { get; init; }
	public string? Name { get; init; }
	public string? Kind { get; init; }
}

// A directed FSM edge. `preconditionArtifact` names a comment-artifact tag (e.g.
// "spec_plan") that must exist on the node before the transition — modeled here,
// enforced by the engine task.
public sealed record MethodologyTransitionInput
{
	public string? From { get; init; }
	public string? To { get; init; }
	public bool RequiresApproval { get; init; }
	public bool RequiresReason { get; init; }
	public string? PreconditionArtifact { get; init; }
}

// One entry of the `migration` argument of tasks_methodology_def_upsert: declared value
// repairs for live nodes on boards of `kind` that a definition change would otherwise
// strand. A mapping applies ONLY where a node's current value is invalid under the NEW
// resolution — a valid value is never rewritten (declarative repair, not bulk rename).
public sealed record MethodologyMigrationInput
{
	// The board-kind slug the mappings repair (a kind of the new definition, a builtin
	// kind, or the slug of a kind the new definition DROPPED — its boards keep the slug).
	public string? Kind { get; init; }
	public MethodologyValueMapInput[]? Types { get; init; }
	public MethodologyValueMapInput[]? Statuses { get; init; }
}

// One {from,to} value repair; `to` must be valid under the new resolution.
public sealed record MethodologyValueMapInput
{
	public string? From { get; init; }
	public string? To { get; init; }
}

// The `sort` argument of tasks_search: `by` names the axis (priority|created|updated|title|
// relevance — relevance only with a query), `desc` flips the direction (ignored for
// relevance, whose fused order is already most-relevant-first).
public sealed record SortInput
{
	public string? By { get; init; }
	public bool Desc { get; init; }
}

// One transcript message as submitted to session_append — the same {role, content} shape the
// snapshot stores and the REST ndjson push sends; the server assigns the ordinal.
public sealed record SessionMessageDto
{
	public string? Role { get; init; }
	public string? Content { get; init; }
}

// An entry as submitted to memory_upsert. Mirrors EXACTLY the fields the old JsonElement parser
// (MemoryTools.ParseEntries) accepted, including the `deleted:true` soft-delete marker.
public sealed record MemoryEntryInputDto
{
	public string? Key { get; init; }
	public long Version { get; init; }
	public string? Type { get; init; }
	public string? Description { get; init; }
	public string? Body { get; init; }

	// Tags as an ARRAY of tag strings (like tasks): null = omit (PATCH: keep the current
	// set), [] = explicit clear, a non-empty list REPLACES the set.
	public IReadOnlyList<string>? Tags { get; init; }

	public string? Metadata { get; init; }
	public string? PrevKey { get; init; }

	// Soft-delete marker: { key, deleted:true } closes the active entry (optional version baseline).
	public bool Deleted { get; init; }
}
