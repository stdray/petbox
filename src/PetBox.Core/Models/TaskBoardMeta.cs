using LinqToDB.Mapping;

namespace PetBox.Core.Models;

// Metadata row for a single named task board. PK is (ProjectKey, Name). The
// actual plan nodes live in `data/tasks/{ProjectKey}/{Name}.db` (temporal table);
// this table tracks which boards exist. Mirrors LogMeta — explicit creation, no
// auto-vivify.
[Table("TaskBoards")]
public sealed record TaskBoardMeta
{
	[Column, PrimaryKey, NotNull]
	public string ProjectKey { get; init; } = string.Empty;

	[Column, PrimaryKey, NotNull]
	public string Name { get; init; } = string.Empty;

	[Column, Nullable]
	public string? Description { get; init; }

	// Board role: free|spec|ideas|intake|work (default free). Drives the workflow
	// (types/statuses/transitions) + invariants/effects via WorkflowCatalog.
	[Column, NotNull]
	public string Kind { get; init; } = "free";

	[Column, NotNull]
	public DateTime CreatedAt { get; init; }

	[Column, NotNull]
	public DateTime UpdatedAt { get; init; }

	// Closed/archived: null = open. A closed board rejects writes (agents stop writing
	// to it by inertia) but stays readable; history is kept.
	[Column, Nullable]
	public DateTime? ClosedAt { get; init; }

	// For a work board: the name of the spec board its tasks link into (task_spec).
	// Makes the work->spec relationship explicit so an agent doesn't guess among several
	// spec boards; specRef targets are validated against this board. Null = unset.
	[Column, Nullable]
	public string? SpecBoard { get; init; }
}
