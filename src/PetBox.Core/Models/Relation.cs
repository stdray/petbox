using LinqToDB.Mapping;

namespace PetBox.Core.Models;

// LEGACY typed edge table in the Core DB (petbox.db). Relations MOVED into the per-project
// tasks file (PetBox.Tasks.Data.Relation → tasks/{project}.db, table `relations`), where the
// endpoints can carry a real FK to the nodes they point at (relations-in-project-db).
//
// This table is deliberately LEFT IN PLACE and is NOT dropped in this change: the drop ships
// as a SEPARATE later release, once the backfill (RelationsToTasksDbMigrator) is verified
// against live data. Until then this record exists ONLY so the migrator can read the source
// rows and ProjectDeletion can sweep them; nothing in the live read/write path uses it.
[Table("Relation")]
public sealed record LegacyRelation
{
	[Column, PrimaryKey, NotNull] public string Id { get; init; } = string.Empty;
	[Column, NotNull] public string ProjectKey { get; init; } = string.Empty;
	[Column, NotNull] public string Kind { get; init; } = string.Empty;
	[Column, NotNull] public string FromNodeId { get; init; } = string.Empty;
	[Column, NotNull] public string ToNodeId { get; init; } = string.Empty;
	[Column, NotNull] public DateTime CreatedAt { get; init; }
	[Column, Nullable] public DateTime? ClosedAt { get; init; }
}
