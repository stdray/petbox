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
	// LEGACY: relations now live in the per-project tasks file (tasks/{project}.db). This table
	// is kept (not dropped) until the backfill is verified on live data — see LegacyRelation.
	public ITable<LegacyRelation> LegacyRelations => this.GetTable<LegacyRelation>();
	public ITable<AgentDefinitionRow> AgentDefinitions => this.GetTable<AgentDefinitionRow>();
	// The LLM registry's own store (M039): endpoints carry their api key as columns, routes are
	// bound to their level by a composite FK. Not yet read by the router — the ConfigBindings-backed
	// LlmRegistryStore still serves production.
	public ITable<LlmEndpointRow> LlmEndpoints => this.GetTable<LlmEndpointRow>();
	public ITable<LlmRouteRow> LlmRoutes => this.GetTable<LlmRouteRow>();

	// Foreign Keys=True turns on per-connection FK enforcement — SQLite defaults it OFF, and an
	// unenforced FK is decoration. core.db had no foreign keys at all until llm_routes' composite FK
	// (Scope, ScopeKey, Endpoint) -> llm_endpoints(Scope, ScopeKey, Name), which is what stops a route
	// at one level from pointing at an endpoint (and therefore an api key) at another. Nothing else in
	// core.db declares a FK, so switching enforcement on changes no other write path. Mirrors TasksDb.
	public static DataOptions<PetBoxDb> CreateOptions(string connectionString)
	{
		if (!connectionString.Contains("Foreign Keys", StringComparison.OrdinalIgnoreCase))
			connectionString = connectionString.TrimEnd(';') + ";Foreign Keys=True";
		return new(new DataOptions().UseSQLite(connectionString).UseMappingSchema(SharedMappingSchema));
	}

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
			.Property(u => u.PasswordHash).HasLength(200).IsNullable(false)
			.Property(u => u.WorkspaceQuota).HasDataType(DataType.Int32).IsNullable(false);

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
			.Property(p => p.Description).HasLength(1000)
			// Same reason as ApiKey.DefaultProjectKey below: an undeclared column on a
			// partially-Fluent entity is dropped from the schema cache, so INSERT silently omits it
			// and every read comes back the CLR default (false) regardless of what was stored —
			// which is exactly the sandbox write gate's containment column (M041), so a dropped
			// mapping here would have made every project look non-sandbox forever.
			.Property(p => p.Sandbox).IsNullable(false);

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
			// Same reason: an undeclared column on a partially-Fluent entity is dropped from the
			// schema cache, so INSERT would omit it and the default project would silently vanish.
			.Property(a => a.DefaultProjectKey).HasLength(100).IsNullable(true)
			.Property(a => a.CreatedAt).IsNullable(false)
			// M042 (spec work/smoke-writes-into-real-projects) — same "dropped from the schema
			// cache" trap: without this, a sandboxOnly key would mint fine but persist/read back
			// SandboxOnly=false, silently defeating the write gate for every minted key.
			.Property(a => a.SandboxOnly).IsNullable(false)
			// M043 (spec apikey-last-used) — declared for the same reason as ExpiresAt above: an
			// undeclared DateTime column is dropped from the schema cache, and the flusher's UPDATE
			// would write a column linq2db then refuses to read back (every key would look unused).
			.Property(a => a.LastUsedAt).HasDataType(DataType.DateTime).IsNullable(true);

		builder.Entity<DataTable>()
			.HasTableName("DataTables")
			.HasPrimaryKey(d => d.Name)
			.Property(d => d.Name).HasLength(200).IsNullable(false)
			.Property(d => d.ProjectKey).HasLength(100).IsNullable(false)
			.Property(d => d.Columns).HasDataType(DataType.Text).IsNullable(false)
			// Real M005 columns (NOT NULL, defaulted) that were never declared here — so linq2db dropped
			// them from the schema cache: INSERT omitted them and every read came back `false`, silently.
			// Found by FluentMappingCompletenessTests, the guard for exactly this class of bug.
			.Property(d => d.Read).IsNullable(false)
			.Property(d => d.Write).IsNullable(false)
			.Property(d => d.Delete).IsNullable(false);

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
