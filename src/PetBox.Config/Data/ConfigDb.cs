using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.Mapping;
using PetBox.Core.Models;

namespace PetBox.Config.Data;

[Table("ConfigBindingHistory")]
public sealed record ConfigBindingHistoryEntry
{
	[Column, Identity, PrimaryKey]
	public long Id { get; init; }
	[Column, NotNull]
	public long BindingId { get; init; }
	[Column, NotNull]
	public string Action { get; init; } = string.Empty;
	[Column, NotNull]
	public string Path { get; init; } = string.Empty;
	[Column, NotNull]
	public string Tags { get; init; } = string.Empty;
	[Column]
	public BindingKind Kind { get; init; }
	[Column]
	public string? OldValue { get; init; }
	[Column]
	public string? NewValue { get; init; }
	[Column, NotNull]
	public string Actor { get; init; } = "system";
	[Column]
	public DateTime At { get; init; }
}

[Table("TagVocabulary")]
public sealed record TagVocabularyEntry
{
	[Column, Identity, PrimaryKey]
	public long Id { get; init; }
	[Column, NotNull]
	public string TagKey { get; init; } = string.Empty;
	[Column]
	public string? Description { get; init; }
	[Column]
	public DateTime CreatedAt { get; init; }
}

public sealed class ConfigDb : DataConnection
{
	public ConfigDb(DataOptions<ConfigDb> options) : base(options.Options) { }

	public ITable<ConfigBinding> Bindings => this.GetTable<ConfigBinding>();
	public ITable<ConfigBindingHistoryEntry> History => this.GetTable<ConfigBindingHistoryEntry>();
	public ITable<TagVocabularyEntry> Tags => this.GetTable<TagVocabularyEntry>();

	public static DataOptions<ConfigDb> CreateOptions(string connectionString) =>
		new(new DataOptions().UseSQLite(connectionString));
}
