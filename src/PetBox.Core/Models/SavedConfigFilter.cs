using LinqToDB.Mapping;

namespace PetBox.Core.Models;

// A named, reusable config tag-filter (e.g. "env=prod,region=ru"), shown as a
// chip on the config page. Workspace-scoped. Mirrors SavedQuery for logs.
[Table("SavedConfigFilters")]
public sealed record SavedConfigFilter
{
	[Column, Identity, PrimaryKey, NotNull]
	public long Id { get; init; }

	[Column, NotNull]
	public string WorkspaceKey { get; init; } = string.Empty;

	[Column, NotNull]
	public string Name { get; init; } = string.Empty;

	// Comma-separated key=value pairs, the same shape as the ?t.k=v facet filter.
	[Column, NotNull]
	public string FilterTags { get; init; } = string.Empty;

	[Column, NotNull]
	public DateTime CreatedAt { get; init; }
}
