using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.Mapping;
using YobaBox.Core.Models;

namespace YobaBox.Core.Data;

public sealed class YobaBoxDb : DataConnection
{
	public YobaBoxDb(DataOptions<YobaBoxDb> options) : base(options.Options)
	{
		ConfigureMapping(MappingSchema);
	}

	public ITable<Project> Projects => this.GetTable<Project>();
	public ITable<Service> Services => this.GetTable<Service>();
	public ITable<ApiKey> ApiKeys => this.GetTable<ApiKey>();
	public ITable<ConfigBinding> ConfigBindings => this.GetTable<ConfigBinding>();

	public static DataOptions<YobaBoxDb> CreateOptions(string connectionString) =>
		new(new DataOptions().UseSQLite(connectionString));

	static void ConfigureMapping(MappingSchema ms)
	{
		var builder = new FluentMappingBuilder(ms);

		builder.Entity<Project>()
			.HasTableName("Projects")
			.HasPrimaryKey(p => p.Key)
			.Property(p => p.Key).HasLength(100).IsNullable(false)
			.Property(p => p.Name).HasLength(200).IsNullable(false)
			.Property(p => p.Description).HasLength(1000);

		builder.Entity<Service>()
			.HasTableName("Services")
			.HasPrimaryKey(s => s.Key)
			.Property(s => s.Key).HasLength(100).IsNullable(false)
			.Property(s => s.ProjectKey).HasLength(100).IsNullable(false)
			.Property(s => s.Kind).HasDataType(DataType.Int32).IsNullable(false)
			.Property(s => s.Url).HasLength(500)
			.Property(s => s.Version).HasLength(50)
			.Property(s => s.ShortSha).HasLength(8)
			.Property(s => s.Health).HasDataType(DataType.Int32).IsNullable(false);

		builder.Entity<ApiKey>()
			.HasTableName("ApiKeys")
			.HasPrimaryKey(a => a.Key)
			.Property(a => a.Key).HasLength(100).IsNullable(false)
			.Property(a => a.ProjectKey).HasLength(100).IsNullable(false)
			.Property(a => a.Scopes).HasDataType(DataType.Text).IsNullable(false)
			.Property(a => a.CreatedAt).IsNullable(false);

		builder.Entity<ConfigBinding>()
			.HasTableName("ConfigBindings")
			.HasIdentity(c => c.Id)
			.Property(c => c.Path).HasLength(500).IsNullable(false)
			.Property(c => c.Value).HasDataType(DataType.Text).IsNullable(false)
			.Property(c => c.Tags).HasLength(1000).IsNullable(false)
			.Property(c => c.CreatedAt).IsNullable(false)
			.Property(c => c.UpdatedAt).IsNullable(false);

		builder.Build();
	}
}
