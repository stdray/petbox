using LinqToDB.Mapping;

namespace YobaBox.Core.Settings;

// Row in the L2 Settings table. PK is (Scope, ScopeKey, Path).
[Table("Settings")]
public sealed record Setting
{
	[Column, PrimaryKey, NotNull]
	public string Scope { get; init; } = string.Empty;

	[Column, PrimaryKey, NotNull]
	public string ScopeKey { get; init; } = string.Empty;

	[Column, PrimaryKey, NotNull]
	public string Path { get; init; } = string.Empty;

	[Column, NotNull]
	public string Type { get; init; } = string.Empty;

	[Column, NotNull]
	public string Value { get; init; } = string.Empty;

	[Column, NotNull]
	public DateTime UpdatedAt { get; init; }

	[Column, Nullable]
	public long? UpdatedBy { get; init; }
}
