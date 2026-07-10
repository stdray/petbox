using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.Mapping;
using PetBox.Core.Models;
using PetBox.Core.Settings;

namespace PetBox.Core.Data;

public sealed class PetBoxDb : DataConnection
{
	public PetBoxDb(DataOptions<PetBoxDb> options) : base(options.Options) { }

	public ITable<Project> Projects => this.GetTable<Project>();
	public ITable<Workspace> Workspaces => this.GetTable<Workspace>();
	public ITable<User> Users => this.GetTable<User>();
	public ITable<WorkspaceMember> WorkspaceMembers => this.GetTable<WorkspaceMember>();
	public ITable<ApiKey> ApiKeys => this.GetTable<ApiKey>();
	public ITable<DataTable> DataTables => this.GetTable<DataTable>();
	public ITable<SavedQuery> SavedQueries => this.GetTable<SavedQuery>();
	public ITable<ShareLink> ShareLinks => this.GetTable<ShareLink>();
	public ITable<Setting> Settings => this.GetTable<Setting>();
	public ITable<DataDb> DataDbs => this.GetTable<DataDb>();
	public ITable<LogMeta> Logs => this.GetTable<LogMeta>();
	public ITable<HealthReport> HealthReports => this.GetTable<HealthReport>();
	public ITable<HealthEndpoint> HealthEndpoints => this.GetTable<HealthEndpoint>();
	public ITable<SavedConfigFilter> SavedConfigFilters => this.GetTable<SavedConfigFilter>();
	public ITable<TaskBoardMeta> TaskBoards => this.GetTable<TaskBoardMeta>();
	public ITable<MemoryStoreMeta> MemoryStores => this.GetTable<MemoryStoreMeta>();
	public ITable<Relation> Relations => this.GetTable<Relation>();
	public ITable<AgentDefinitionRow> AgentDefinitions => this.GetTable<AgentDefinitionRow>();

	public static DataOptions<PetBoxDb> CreateOptions(string connectionString) =>
		new(new DataOptions().UseSQLite(connectionString).UseMappingSchema(SharedMappingSchema));

	// Fluent mapping is built ONCE into this shared schema and handed to every
	// connection via DataOptions.UseMappingSchema. The previous approach rebuilt it
	// in the constructor against each instance's own MappingSchema; since PetBoxDb is
	// AddScoped (one per request), every request created a fresh MappingSchema and
	// linq2db's per-schema MappingAttributesCache grew without bound (~290 MB / 3M+
	// nodes by mid-day, driving the prod OOM — see roadmap ops/petbox-mem-profiling).
	static readonly MappingSchema SharedMappingSchema = BuildMappingSchema();

	static MappingSchema BuildMappingSchema()
	{
		var ms = new MappingSchema();
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

		builder.Entity<ApiKey>()
			.HasTableName("ApiKeys")
			.HasPrimaryKey(a => a.Key)
			.Property(a => a.Key).HasLength(100).IsNullable(false)
			.Property(a => a.ProjectKey).HasLength(100).IsNullable(false)
			.Property(a => a.Scopes).HasDataType(DataType.Text).IsNullable(false)
			.Property(a => a.Name).HasLength(200).IsNullable(false)
			// Explicitly declared so it isn't dropped from the schema cache (see ShareLink note
			// above) — otherwise ExpiresAt reads back null and Where(...ExpiresAt...) won't translate.
			.Property(a => a.ExpiresAt).HasDataType(DataType.DateTime).IsNullable(true)
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
			.Property(q => q.ProjectKey).HasLength(100).IsNullable(false)
			// CreatedAt/UpdatedAt must be declared explicitly for the same reason as
			// ApiKey.ExpiresAt/ShareLink below: with a partially-Fluent entity linq2db
			// drops the undeclared DateTime columns from the schema cache, so INSERT
			// omitted them and the NOT NULL constraint failed → "Save as" 500'd
			// (work card ui-saved-query-500).
			.Property(q => q.CreatedAt).HasDataType(DataType.DateTime).IsNullable(false)
			.Property(q => q.UpdatedAt).HasDataType(DataType.DateTime).IsNullable(false);

		// ShareLink has all-[Column] attributes but mixing attribute-based mapping
		// with FluentMapping for other entities seems to drop some columns from
		// the schema cache — same root cause as the Service.CheckedAt issue.
		// Explicit Fluent declaration unblocks linq2db's `Where(...ExpiresAt...)`.
		builder.Entity<ShareLink>()
			.HasTableName("ShareLinks")
			.HasPrimaryKey(s => s.Id)
			.Property(s => s.Id).HasLength(100).IsNullable(false)
			.Property(s => s.ProjectKey).HasLength(100).IsNullable(false)
			.Property(s => s.LogName).HasLength(100).IsNullable(false)
			.Property(s => s.Kql).HasDataType(DataType.Text).IsNullable(false)
			.Property(s => s.CreatedAt).HasDataType(DataType.DateTime).IsNullable(false)
			.Property(s => s.ExpiresAt).HasDataType(DataType.DateTime).IsNullable(false)
			.Property(s => s.SaltBase64).HasDataType(DataType.Text).IsNullable(false)
			.Property(s => s.ColumnsJson).HasDataType(DataType.Text).IsNullable(false)
			.Property(s => s.ModesJson).HasDataType(DataType.Text).IsNullable(false)
			.Property(s => s.CreatedBy).HasLength(100).IsNullable(false);

		builder.Build();
		return ms;
	}
}
