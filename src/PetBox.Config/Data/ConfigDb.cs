using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.Mapping;
using PetBox.Core.Models;

namespace PetBox.Config.Data;

[Table("ConfigBindingHistory")]
public sealed record ConfigBindingHistoryEntry
{
	[Identity, PrimaryKey]
	public long Id { get; init; }
	public long BindingId { get; init; }
	public string Action { get; init; } = string.Empty;
	public string Path { get; init; } = string.Empty;
	public string Tags { get; init; } = string.Empty;
	public BindingKind Kind { get; init; }
	public string? OldValue { get; init; }
	public string? NewValue { get; init; }
	public string Actor { get; init; } = "system";
	public DateTime At { get; init; }
}

[Table("TagVocabulary")]
public sealed record TagVocabularyEntry
{
	[Identity, PrimaryKey]
	public long Id { get; init; }
	public string TagKey { get; init; } = string.Empty;
	public string? Description { get; init; }
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
