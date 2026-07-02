namespace PetBox.Web.Mcp.Contract;

// Typed MCP tool-INPUT records (typed-surface Phase 4 — typed inputs). The structural
// array params of tasks.upsert (`nodes`) and memory.upsert (`entries`) used to arrive as a
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

// A node as submitted to tasks.upsert. Mirrors EXACTLY the fields the old JsonElement parser
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
	// version baseline). Mirrors memory.upsert's marker; other fields are ignored.
	public bool Deleted { get; init; }
}

// The `sort` argument of tasks.search: `by` names the axis (priority|created|updated|title|
// relevance — relevance only with a query), `desc` flips the direction (ignored for
// relevance, whose fused order is already most-relevant-first).
public sealed record SortInput
{
	public string? By { get; init; }
	public bool Desc { get; init; }
}

// One transcript message as submitted to session.append — the same {role, content} shape the
// snapshot stores and the REST ndjson push sends; the server assigns the ordinal.
public sealed record SessionMessageDto
{
	public string? Role { get; init; }
	public string? Content { get; init; }
}

// An entry as submitted to memory.upsert. Mirrors EXACTLY the fields the old JsonElement parser
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
