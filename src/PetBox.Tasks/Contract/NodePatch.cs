namespace PetBox.Tasks.Contract;

// A single node as submitted to ITasksService.UpsertAsync. The adapter (MCP/REST/UI)
// owns input parsing and addressing — it resolves the node's path to a canonical Key
// (and PrevKey for a rename) before handing it here. Read-merge semantics live in the
// service: a string field that is null was OMITTED (inherit the prior active row),
// while a non-null value (including "") is an explicit set/clear. Priority null = omit.
// CommitRef is nullable on the row, so its presence is carried by CommitRefSet.
public sealed record NodePatch
{
	// Canonical "l1[/l2[/l3]]" key, already validated/canonicalised by the adapter.
	public required string Key { get; init; }
	// Canonical key of the node being renamed from, or null.
	public string? PrevKey { get; init; }
	// Baseline version the author last saw (0 = new) — drives optimistic concurrency.
	public long Version { get; init; }

	public string? Status { get; init; }
	public string? Type { get; init; }
	public string? Title { get; init; }
	public string? Body { get; init; }
	public bool CommitRefSet { get; init; }
	public string? CommitRef { get; init; }
	public long? Priority { get; init; }

	// Per-node link fields. specRef → the spec NodeId this task implements (task_spec).
	// blockedBy → a NodeId that blocks this task (blocks). Null/empty = no link given.
	public string? SpecRef { get; init; }
	public string? BlockedBy { get; init; }

	// Enforced tags ("namespace:value", namespaces area|concern). null = OMIT (leave the
	// node's tags as-is); a non-null list (incl. empty) REPLACES the node's full tag set.
	public IReadOnlyList<string>? Tags { get; init; }
}
