using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.Mapping;
using YobaBox.Core.Models;
using YobaBox.Core.Settings;

namespace YobaBox.Core.Data;

public sealed class YobaBoxDb : DataConnection
{
	public YobaBoxDb(DataOptions<YobaBoxDb> options) : base(options.Options)
	{
		ConfigureMapping(MappingSchema);
	}

	public ITable<Project> Projects => this.GetTable<Project>();
	public ITable<Workspace> Workspaces => this.GetTable<Workspace>();
	public ITable<User> Users => this.GetTable<User>();
	public ITable<WorkspaceMember> WorkspaceMembers => this.GetTable<WorkspaceMember>();
	public ITable<Service> Services => this.GetTable<Service>();
	public ITable<ApiKey> ApiKeys => this.GetTable<ApiKey>();
	public ITable<DataTable> DataTables => this.GetTable<DataTable>();
	public ITable<SavedQuery> SavedQueries => this.GetTable<SavedQuery>();
	public ITable<ShareLink> ShareLinks => this.GetTable<ShareLink>();
	public ITable<RetentionPolicy> RetentionPolicies => this.GetTable<RetentionPolicy>();
	public ITable<Setting> Settings => this.GetTable<Setting>();

	public static DataOptions<YobaBoxDb> CreateOptions(string connectionString) =>
		new(new DataOptions().UseSQLite(connectionString));

	static void ConfigureMapping(MappingSchema ms)
	{
		var builder = new FluentMappingBuilder(ms);

		builder.Entity<Workspace>()
			.HasTableName("Workspaces")
			.HasPrimaryKey(w => w.Key)
			.Property(w => w.Key).HasLength(100).IsNullable(false)
			.Property(w => w.Name).HasLength(200).IsNullable(false)
			.Property(w => w.Description).HasLength(1000);

		builder.Entity<User>()
			.HasTableName("Users")
			.HasIdentity(u => u.Id)
			.Property(u => u.Id).IsPrimaryKey()
			.Property(u => u.Username).HasLength(100).IsNullable(false)
			.Property(u => u.PasswordHash).HasLength(200).IsNullable(false);

		builder.Entity<WorkspaceMember>()
			.HasTableName("WorkspaceMembers")
			.HasIdentity(m => m.Id)
			.Property(m => m.Id).IsPrimaryKey()
			.Property(m => m.UserId).IsNullable(false)
			.Property(m => m.WorkspaceKey).HasLength(100).IsNullable(false)
			.Property(m => m.Role).HasDataType(DataType.Int32).IsNullable(false);

		builder.Entity<Project>()
			.HasTableName("Projects")
			.HasPrimaryKey(p => p.Key)
			.Property(p => p.Key).HasLength(100).IsNullable(false)
			.Property(p => p.WorkspaceKey).HasLength(100).IsNullable(false)
			.Property(p => p.Name).HasLength(200).IsNullable(false)
			.Property(p => p.Description).HasLength(1000);

		builder.Entity<Service>()
			.HasTableName("Services")
			.HasPrimaryKey(s => s.Key)
			.Property(s => s.Key).HasLength(100).IsNullable(false)
			.Property(s => s.ProjectKey).HasLength(100).IsNullable(false)
			.Property(s => s.HealthModel).HasDataType(DataType.Int32).IsNullable(false)
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

		builder.Entity<DataTable>()
			.HasTableName("DataTables")
			.HasPrimaryKey(d => d.Name)
			.Property(d => d.Name).HasLength(200).IsNullable(false)
			.Property(d => d.ProjectKey).HasLength(100).IsNullable(false)
			.Property(d => d.Columns).HasDataType(DataType.Text).IsNullable(false);

		builder.Entity<SavedQuery>()
			.HasTableName("SavedQueries")
			.HasIdentity(q => q.Id)
			.Property(q => q.Id).IsPrimaryKey()
			.Property(q => q.Name).HasLength(200).IsNullable(false)
			.Property(q => q.Kql).HasDataType(DataType.Text).IsNullable(false)
			.Property(q => q.ProjectKey).HasLength(100).IsNullable(false);

		// ShareLink и RetentionPolicy самомаппятся через [Table]/[Column] атрибуты — здесь не нужны.

		builder.Build();
	}
}
