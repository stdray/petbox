using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Seq.Extensions.Logging;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Data;
using PetBox.LlmRouter;
using PetBox.Log.Core;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Ingestion;
using PetBox.Web;
using PetBox.Web.Contract;
using PetBox.Web.Mcp;
using PetBox.Web.Health;
using PetBox.Web.Ingestion;
using PetBox.Web.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PetBox.Core.Models;

if (args.Length >= 2 && args[0] == "--hash-password")
{
	Console.WriteLine(AdminPasswordHasher.Hash(args[1]));
	return;
}

var builder = WebApplication.CreateBuilder(args);
ConfigureServices(builder);
var app = builder.Build();
Configure(app);
app.Run();

public partial class Program
{
	// True when the build-time OpenAPI generator (Microsoft.Extensions.ApiDescription.Server)
	// is driving startup. GetDocument.Insider loads this app's entry-point and runs the host
	// (through StartAsync) to read endpoint metadata; it sets itself as the entry assembly.
	// Used to redirect internal SQLite stores to a temp dir so document generation leaves no
	// migrated db / backups / self-log in the working tree (see ConfigureServices).
	static bool IsOpenApiDocumentGeneration =>
		System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

	public static void ConfigureServices(WebApplicationBuilder builder)
	{
		// Defense-in-depth against the captive-dependency class (see CaptiveDependencyTests,
		// PetBox.Sessions.Episodic.DuckDbSessionEpisodicIndex fix): ValidateScopes makes a
		// singleton that reaches a Scoped service THROW instead of silently sharing one root
		// instance across every concurrent request. This is NOT a startup gate — a captive taken
		// inside a factory lambda (the shape the real bug had) is invisible to ValidateOnBuild,
		// and a singleton registered via a factory is constructed LAZILY, on first resolution, not
		// at builder.Build(). So this flag does not fail the boot; it fails the first request that
		// touches the offending singleton, loudly, instead of quietly corrupting concurrent state.
		// The actual startup-time gate is CaptiveDependencyTests in CI, which force-resolves every
		// singleton up front. ValidateOnBuild is turned on alongside it: it IS free (a pure ctor
		// signature walk against the registered graph, no factory lambdas invoked) and catches a
		// different, complementary shape — a plain ctor-injected type mismatch or an unregistered
		// dependency — for free, at builder.Build() time, in every environment (both flags default
		// to off outside Development).
		builder.Host.UseDefaultServiceProvider(o =>
		{
			o.ValidateScopes = true;
			o.ValidateOnBuild = true;
		});

		// Build-time OpenAPI generation (GetDocument.Insider) hosts this entry-point all the way
		// through app.Run() — it lets StartAsync run (migrations + hosted services fire) and only
		// then aborts before serving requests. Left alone it would migrate ./data/petbox.db and
		// drop backups/self-log files straight into the repo. Redirect every internal SQLite store
		// to a throwaway temp dir so the host still starts cleanly (no "no such table" crashes from
		// hosted services) but nothing touches the working tree. Highest-precedence in-memory
		// provider, added before any config is read below.
		if (IsOpenApiDocumentGeneration)
		{
			var docGenDataDir = Path.Combine(Path.GetTempPath(), "petbox-openapi-doc-" + Guid.NewGuid().ToString("N")[..8]);
			builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:PetBox"] = $"Data Source={docGenDataDir}/petbox.db;Cache=Shared",
			});
		}

		// Honor X-Forwarded-Proto/Host/For from the reverse proxy (Caddy, TLS-terminating, in
		// the Docker network). Clear the known-proxy/network allowlists: the app is only
		// reachable via Caddy, whose source IP isn't loopback, so default trust would drop the
		// headers and leave Request.Scheme as the internal http.
		builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
		{
			o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
				| Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
			o.KnownIPNetworks.Clear();
			o.KnownProxies.Clear();
		});

		// Resolve connection string LAZILY at instantiation time via DI — not capturing
		// builder.Configuration here, because under WebApplicationFactory the test's
		// ConfigureAppConfiguration callback runs DURING builder.Build(), which is
		// AFTER ConfigureServices has already executed. Reading builder.Configuration
		// inline would give the default (./data/petbox.db) instead of the test override.
		// Each factory resolves IConfiguration from its sp parameter, which sees the
		// fully composed config including all test providers.
		static string ResolveCs(IServiceProvider sp) =>
			sp.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")
				?? "Data Source=./data/petbox.db;Cache=Shared";
		static string ResolveDataDir(IServiceProvider sp) =>
			Path.GetDirectoryName(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(ResolveCs(sp)).DataSource)!;

		// CreateDirectory still uses builder.Configuration here — fine because we're
		// only ensuring the default path exists ahead of any first-write; test paths
		// live in Path.GetTempPath() which already exists.
		var bootstrapCs = builder.Configuration.GetConnectionString("PetBox")
			?? "Data Source=./data/petbox.db;Cache=Shared";
		Directory.CreateDirectory(Path.GetDirectoryName(
			new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(bootstrapCs).DataSource)!);

		// core.db is reached ONLY through ICoreDbFactory: `using var db = factory.Open()` per call,
		// giving a fresh caller-owned connection instead of a request-shared one. A linq2db
		// DataConnection is not thread-safe, so a scoped PetBoxDb registration would hand the SAME
		// connection to every thread a request fans out onto (the cross-scope search 500s) — that
		// registration is gone now, and DbInjectionGuardTests asserts it stays gone. SINGLETON here:
		// the factory holds only DataOptions, never a connection.
		builder.Services.AddSingleton<ICoreDbFactory>(sp => new CoreDbFactory(ResolveCs(sp)));
		// Workspace creation + the allowance that gates it. The page handlers get THIS, not a
		// connection: core.db stops at the service boundary, and the quota is enforced by the write
		// itself (the check is welded into the INSERT), not by whoever rendered the button.
		//
		// SCOPED, not singleton: it now claims the quota slot through IWorkspaceMembershipService
		// (which is scoped — it is the one door to WorkspaceMembers, and the seam a cache would live
		// behind), and a singleton holding a scoped service is a captive dependency —
		// CaptiveDependencyTests fails the build on one. It is stateless, so a per-request instance
		// costs nothing; every consumer (the create pages, WorkspaceAdminService, the
		// WorkspaceCreate authorization handler) is already scoped.
		builder.Services.AddScoped<WorkspaceProvisioning>();
		// The catalog of projects/entities (core.db) — the SOURCE OF TRUTH the background enrichment
		// jobs ask "which projects exist" (spec: catalog-is-source-of-truth). Per-project SQLite files
		// are created lazily, so a job that enumerated `{tier}/*.db` was blind to a project without a
		// file yet and kept working the ghost file of a deleted one. Scoped: it reads PetBoxDb.
		builder.Services.AddScoped<IProjectCatalog, ProjectCatalog>();
		// Portable agent-definition store (Core DB, always on — no feature flag).
		builder.Services.AddScoped<PetBox.Core.Services.IAgentDefinitionService, PetBox.Core.Services.AgentDefinitionService>();
		// Write door to the append-only HealthReports table, for the push endpoint (POST /api/health):
		// the handler is pipeline code and does not open core.db itself.
		// Log dbs live under data/logs/** — the one subtree Backup deliberately skips
		// (telemetry, not data; see Backup.ExcludedLogsDirName). Named constant, not a
		// literal, so the two can't drift apart.
		builder.Services.AddSingleton<IScopedDbFactory<LogDb>>(sp => new ScopedDbFactory<LogDb>(
				Path.Combine(ResolveDataDir(sp), PetBox.Core.Data.Backup.ExcludedLogsDirName), PetBox.Core.Settings.Scope.Project,
				cs => new LogDb(LogDb.CreateOptions(cs)), LogSchema.Ensure));
		builder.Services.AddScoped<ILogStore, LogStore>();
		builder.Services.AddScoped<PetBox.Log.Core.Query.ILogQueryService, PetBox.Log.Core.Query.LogQueryService>();
		// The saved-KQL-query door (Pages/Logs/Index.cshtml.cs) — SavedQueries had no owner before this.
		builder.Services.AddScoped<PetBox.Log.Core.Data.ISavedQueryStore, PetBox.Log.Core.Data.SavedQueryStore>();
		builder.Services.AddSingleton<IScopedDbFactory<ConfigDb>>(sp => new ScopedDbFactory<ConfigDb>(
				Path.Combine(ResolveDataDir(sp), "config"), PetBox.Core.Settings.Scope.Workspace,
				cs => new ConfigDb(ConfigDb.CreateOptions(cs)), ConfigSchema.Ensure));
		builder.Services.AddSingleton<IConfigDbFactory>(sp => new ConfigDbFactory(sp.GetRequiredService<IScopedDbFactory<ConfigDb>>()));
		// THE service layer for PetBox.Config (SavedConfigFilters in core.db + ConfigBinding CRUD/
		// resolve in ConfigDb) — Pages.Config.IndexModel and ConfigApi's REST surface both go
		// through this instead of holding a factory (db-out-of-pages-remaining-24).
		builder.Services.AddScoped<PetBox.Config.IConfigDirectory, PetBox.Config.ConfigDirectory>();
		builder.Services.AddSingleton<PetBox.Data.IDataDbFactory>(sp => new PetBox.Data.DataDbFactory(Path.Combine(ResolveDataDir(sp), "db")));
		builder.Services.AddSingleton<PetBox.Data.Schema.SchemaRunner>();
		builder.Services.AddScoped<PetBox.Data.Contract.IDataSqlService, PetBox.Data.Services.DataSqlService>();
		// Tasks / Memory / Sessions — scope-keyed temporal stores. Registered
		// unconditionally (like the log factory); feature flags gate nav, pages and
		// MCP tools, not DI. Each named board/store is its own file; sessions are a
		// single per-project file.
		builder.Services.AddSingleton<IScopedDbFactory<PetBox.Tasks.Data.TasksDb>>(sp => new ScopedDbFactory<PetBox.Tasks.Data.TasksDb>(
				Path.Combine(ResolveDataDir(sp), "tasks"), PetBox.Core.Settings.Scope.Project,
				cs => new PetBox.Tasks.Data.TasksDb(PetBox.Tasks.Data.TasksDb.CreateOptions(cs)), PetBox.Tasks.Data.TasksSchema.Ensure));
		builder.Services.AddScoped<PetBox.Tasks.Data.ITaskBoardStore, PetBox.Tasks.Data.TaskBoardStore>();
		builder.Services.AddScoped<PetBox.Tasks.Data.IRelationStore, PetBox.Tasks.Data.RelationStore>();
		builder.Services.AddScoped<PetBox.Tasks.Data.ITagStore, PetBox.Tasks.Data.TagStore>();
		builder.Services.AddScoped<PetBox.Tasks.Contract.ITasksService, PetBox.Tasks.Services.TasksService>();
		builder.Services.AddScoped<PetBox.Tasks.Contract.ICommentService, PetBox.Tasks.Services.CommentService>();
		builder.Services.AddSingleton<IScopedDbFactory<PetBox.Memory.Data.MemoryDb>>(sp => new ScopedDbFactory<PetBox.Memory.Data.MemoryDb>(
				Path.Combine(ResolveDataDir(sp), "memory"), PetBox.Core.Settings.Scope.Project,
				cs => new PetBox.Memory.Data.MemoryDb(PetBox.Memory.Data.MemoryDb.CreateOptions(cs)), PetBox.Memory.Data.MemorySchema.Ensure));
		builder.Services.AddScoped<PetBox.Memory.Data.IMemoryStore, PetBox.Memory.Data.MemoryStore>();
		// Search relevance re-ranking policy (freshness decay + MMR diversity) — bound from the
		// `Search` section (Search:Recency:*, Search:Diversity:*, Search:Floor:*); enabled with
		// conservative defaults when absent. Now carries the semantic floor too, and is injected
		// into MemoryService AND TasksService (each optional ctor param resolves this singleton;
		// SessionSearchService reuses it as well).
		builder.Services.AddSingleton(
			builder.Configuration.GetSection("Search").Get<PetBox.Core.Search.SearchRerankOptions>() ?? new PetBox.Core.Search.SearchRerankOptions());
		builder.Services.AddScoped<PetBox.Memory.Contract.IMemoryService, PetBox.Memory.Services.MemoryService>();
		// Usage telemetry intake (spec: memory-usage-observability): singleton queue+drain;
		// called ONLY by the MCP/UI adapters, so internal machine traffic never counts.
		builder.Services.AddSingleton<PetBox.Memory.Contract.IMemoryUsageRecorder, PetBox.Memory.Services.MemoryUsageRecorder>();
		// Background enrichment of search indexes — the entity write path never blocks on
		// embedding/tokenization; this drains the temporal log out-of-band. Each module
		// contributes an IBackgroundIndexJob (most materialize vectors, but not all — see
		// SessionTermIndexJob); memory's is registered here, tasks' in the follow-up.
		// Unconditional like the stores it serves.
		builder.Services.AddScoped<PetBox.Web.Search.IBackgroundIndexJob, PetBox.Web.Search.MemoryVectorizationJob>();
		builder.Services.AddScoped<PetBox.Web.Search.IBackgroundIndexJob, PetBox.Web.Search.TasksVectorizationJob>();
		builder.Services.AddGatedHostedService<PetBox.Web.Search.SearchEnrichmentService>();
		// Manual reindex (the `search_reindex` MCP tool): rewinds a project's Class-B cursors and
		// clears its dead-letter so the stock drain above re-embeds the whole corpus. Never runs on
		// its own — an operator asks for it, and it refuses if Embed has no route.
		builder.Services.AddScoped<PetBox.Web.Search.SearchReindexService>();
		// LLM router: neutral ILlmClient (embed/rerank/chat) + ILlmRegistryAdmin over a
		// config-stored endpoint/route registry. Unconditional DI; Feature.LlmRouter gates
		// the MCP surface, not registration.
		builder.Services.AddLlmRouter();
		builder.Services.AddSingleton<IScopedDbFactory<PetBox.Sessions.Data.SessionsDb>>(sp => new ScopedDbFactory<PetBox.Sessions.Data.SessionsDb>(
				Path.Combine(ResolveDataDir(sp), "sessions"), PetBox.Core.Settings.Scope.Project,
				cs => new PetBox.Sessions.Data.SessionsDb(PetBox.Sessions.Data.SessionsDb.CreateOptions(cs)), PetBox.Sessions.Data.SessionsSchema.Ensure));
		builder.Services.AddScoped<PetBox.Sessions.Data.ISessionStore, PetBox.Sessions.Data.SessionStore>();
		builder.Services.AddScoped<PetBox.Sessions.Contract.ISessionService, PetBox.Sessions.Services.SessionService>();
		// Verbatim per-session term index (spec: session-discovery-verbatim): chat-free, so it
		// is registered as its own IBackgroundIndexJob (SessionTermIndexJob) rather than folded
		// into the digest job's LLM/quiet-period gates — a plain tokenization pass has no
		// reason to wait on either.
		builder.Services.AddScoped<PetBox.Sessions.Search.SessionTermIndex>();
		builder.Services.AddScoped<PetBox.Sessions.Search.ISessionTermIndex>(sp => sp.GetRequiredService<PetBox.Sessions.Search.SessionTermIndex>());
		builder.Services.AddScoped<PetBox.Web.Search.IBackgroundIndexJob, PetBox.Web.Search.SessionTermIndexJob>();
		// Session discovery digests: distills each session's transcript into the project's
		// `session-digests` memory store off the write path — rides the same enrichment tick
		// as the vector jobs. Registered after sessions/memory/llm, which it consumes.
		builder.Services.AddScoped<PetBox.Web.Search.IBackgroundIndexJob, PetBox.Web.Search.SessionDigestJob>();
		// Autocapture: distills durable typed facts from settled sessions into the
		// quarantined `autocaptured` memory store (dedup via hybrid neighbors + LLM judge).
		// Dedup thresholds + periodic re-collapse interval are config-tunable (spec: memoverhaul).
		builder.Services.Configure<PetBox.Web.Search.AutocaptureDedupOptions>(
			builder.Configuration.GetSection("AutocaptureDedup"));
		builder.Services.AddScoped<PetBox.Web.Search.IBackgroundIndexJob, PetBox.Web.Search.SessionFactsJob>();
		// Cross-session behavior-pattern mining over the accumulated distillates —
		// registered AFTER the facts job so a tick mines the freshest observations.
		builder.Services.AddScoped<PetBox.Web.Search.IBackgroundIndexJob, PetBox.Web.Search.BehaviorPatternJob>();
		// Quarantine self-cleaning: retires aged autocaptured facts that earned nothing — either
		// never reached at all, or an expensive-and-off-target "noise boar" (>= MinDeliveredChars
		// of body delivered at a mean fit below MaxAvgKRel over the trailing UsageWindowDays).
		// Report-only by default (structured log of candidates); enforce is opt-in via config.
		// MinAge default 30d. Rides the enrichment tick, self-throttled via a singleton clock
		// (the job itself is scoped — a fresh instance per tick).
		builder.Services.AddSingleton<PetBox.Web.Search.MemoryQuarantineGcClock>();
		builder.Services.AddScoped<PetBox.Web.Search.IBackgroundIndexJob>(sp => new PetBox.Web.Search.MemoryQuarantineGcJob(
			sp.GetRequiredService<IProjectCatalog>(),
			sp.GetRequiredService<PetBox.Memory.Contract.IMemoryService>(),
			sp.GetService<Microsoft.Extensions.Logging.ILogger<PetBox.Web.Search.MemoryQuarantineGcJob>>(),
			minAge: builder.Configuration.GetValue<int?>("Memory:QuarantineGc:MinAgeDays") is { } days ? TimeSpan.FromDays(days) : null,
			enforce: builder.Configuration.GetValue("Memory:QuarantineGc:Enforce", false),
			clock: sp.GetRequiredService<PetBox.Web.Search.MemoryQuarantineGcClock>(),
			usageWindow: builder.Configuration.GetValue<int?>("Memory:QuarantineGc:UsageWindowDays") is { } wd ? TimeSpan.FromDays(wd) : null,
			minDeliveredChars: builder.Configuration.GetValue<long?>("Memory:QuarantineGc:MinDeliveredChars"),
			maxAvgKRel: builder.Configuration.GetValue<double?>("Memory:QuarantineGc:MaxAvgKRel")));
		// Two-stage session search: digest discovery (memory) → episodic hydration. Discovery
		// re-ranking reuses the shared `Search:Recency`/`Search:Diversity` policy (already a
		// singleton above) for decay + MMR; the session-specific semantic-noise floor binds from
		// `Search:Sessions:*` (conservative default when absent — spec search-fair-fusion).
		builder.Services.AddSingleton(
			builder.Configuration.GetSection("Search:Sessions").Get<PetBox.Web.Search.SessionSearchOptions>() ?? new PetBox.Web.Search.SessionSearchOptions());
		// Full-scan escape hatch (spec: session-fullscan-optin): opt-in only, permission-gated
		// (SessionFullScanSettings, resolved via ISettingsResolver — system AND project must
		// both allow it). The scan cap binds from `Search:Sessions:FullScan:*`.
		builder.Services.AddSingleton(
			builder.Configuration.GetSection("Search:Sessions:FullScan").Get<PetBox.Sessions.Search.SessionFullScanOptions>() ?? new PetBox.Sessions.Search.SessionFullScanOptions());
		builder.Services.AddScoped<PetBox.Sessions.Search.ISessionFullScanIndex, PetBox.Sessions.Search.SessionFullScanIndex>();
		builder.Services.AddScoped<PetBox.Web.Search.SessionSearchService>(sp => new PetBox.Web.Search.SessionSearchService(
			sp.GetRequiredService<PetBox.Memory.Contract.IMemoryService>(),
			sp.GetRequiredService<PetBox.Sessions.Contract.ISessionEpisodicIndex>(),
			sp.GetRequiredService<PetBox.Sessions.Search.ISessionTermIndex>(),
			sp.GetRequiredService<PetBox.Sessions.Search.ISessionFullScanIndex>(),
			sp.GetRequiredService<PetBox.Core.Settings.ISettingsResolver>(),
			sp.GetRequiredService<PetBox.Sessions.Contract.ISessionService>(),
			sp.GetRequiredService<PetBox.Core.Search.SearchRerankOptions>(),
			sp.GetRequiredService<PetBox.Web.Search.SessionSearchOptions>()));
		// Episodic tier: transient per-session DuckDB index, hydrated on demand and aged
		// out by idleness. Singleton — it IS the hydration cache. The stage-2 in-session
		// fair-fusion knobs (junk-exclusion min length + semantic-noise floor, spec
		// search-fair-fusion) bind from `Search:Episodic:*` — sibling of the stage-1
		// `Search:Sessions:*` floor above; conservative defaults when absent.
		// The LLM client is taken as an IServiceScopeFactory, NOT as an ILlmClient: ILlmClient is
		// SCOPED (CapabilityRouter → ILlmRegistryLevelResolver → PetBoxDb), and a singleton that
		// resolves it here would capture ONE root-scoped PetBoxDb for the life of the process —
		// shared by every concurrent session_search and never disposed. The index rents a client
		// from a fresh scope per search instead (see CaptiveDependencyTests, which fails the build
		// if any singleton's graph reaches a scoped service).
		builder.Services.AddSingleton<PetBox.Sessions.Contract.ISessionEpisodicIndex>(sp =>
			new PetBox.Sessions.Episodic.DuckDbSessionEpisodicIndex(
				sp.GetRequiredService<IScopedDbFactory<PetBox.Sessions.Data.SessionsDb>>(),
				sp.GetRequiredService<IServiceScopeFactory>(),
				sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PetBox.Sessions.Episodic.DuckDbSessionEpisodicIndex>>(),
				options: builder.Configuration.GetSection("Search:Episodic").Get<PetBox.Sessions.Contract.SessionEpisodicOptions>()
					?? new PetBox.Sessions.Contract.SessionEpisodicOptions()));
		// Deploy: single FLEET-WIDE mutable db (one node hosts containers from many
		// projects, so NOT per-project scoped). Schema ensured once at startup in Configure().
		// DeployDb itself is deliberately NOT registered: the ONLY way to a connection is
		// IDeployDbFactory.Open(), which yields a fresh caller-owned one. An unregistered type cannot
		// be injected anywhere — not a ctor, not a minimal-API handler parameter, not an MCP tool
		// method, not GetRequiredService — so "a scoped DataConnection shared across the threads a
		// request fans out onto" stops being expressible rather than merely being absent today.
		// DbInjectionGuardTests fails the build if the registration ever comes back.
		builder.Services.AddSingleton<PetBox.Deploy.Data.IDeployDbFactory>(sp => new PetBox.Deploy.Data.DeployDbFactory(
			$"Data Source={Path.Combine(ResolveDataDir(sp), "deploy.db")};Cache=Shared"));
		builder.Services.AddScoped<PetBox.Deploy.Contract.IDeployService, PetBox.Deploy.Services.DeployService>();
		// The node-agent's server-side half (poll + enroll). It straddles the deploy db, core.db and
		// the config db, which is exactly why it is a service and not an endpoint lambda.
		builder.Services.AddScoped<PetBox.Web.Deploy.IDeployAgentService, PetBox.Web.Deploy.DeployAgentService>();
		if (new FeatureFlags(builder.Configuration).IsEnabled(Feature.Deploy))
			builder.Services.AddGatedHostedService<PetBox.Deploy.Services.DeployFailoverSweeper>();
		// Orphan file reclamation for the per-project temporal stores (tasks / memory /
		// sessions). ProjectDeletion cascades away a deleted project's Core-DB metadata but
		// leaves its on-disk `.db` files behind; these mop them up eventually-consistently,
		// mirroring the Data/Log orphan services. Unconditional data hygiene (like
		// BackupService) — the factories above are unconditional, and a file can outlive a
		// since-disabled feature.
		builder.Services.AddGatedHostedService<PetBox.Tasks.Data.TaskBoardOrphanCleanupService>();
		builder.Services.AddGatedHostedService<PetBox.Memory.Data.MemoryOrphanCleanupService>();
		builder.Services.AddGatedHostedService<PetBox.Sessions.Data.SessionOrphanCleanupService>();
		// Periodic VACUUM INTO snapshots of every internal db; unconditional (data
		// safety is cross-cutting, not feature-gated).
		builder.Services.AddGatedHostedService(sp => new PetBox.Core.Data.BackupService(
			ResolveDataDir(sp),
			sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PetBox.Core.Data.BackupService>>()));
		var masterKey = builder.Configuration["PetBox:MasterKey"]
			?? Environment.GetEnvironmentVariable("PETBOX_MASTER_KEY");
		builder.Services.AddSingleton(Options.Create(new SecretEncryptorOptions { MasterKey = masterKey }));
		builder.Services.AddSingleton<ISecretEncryptor, AesGcmSecretEncryptor>();
		builder.Services.AddMemoryCache();
		builder.Services.AddSingleton<CleFParser>();
		builder.Services.AddSingleton<ITailBroadcaster, InMemoryTailBroadcaster>();
		// IngestionSettings / LogSettings / DashboardSettings — Phase 23.3 L2 records.
		// Consumers read via ISettingsResolver; no IOptions bindings here.
		if (new FeatureFlags(builder.Configuration).IsEnabled(Feature.Logging))
		{
			builder.Services.AddSingleton<ChannelIngestionPipeline>();
			builder.Services.AddSingleton<IIngestionPipeline>(sp => sp.GetRequiredService<ChannelIngestionPipeline>());
			// Registered TWICE — write side (IIngestionPipeline, above) and drain side (hosted,
			// below). Host:BackgroundServices=false suppresses only the DRAIN: the pipeline still
			// resolves and still accepts enqueues, they just never reach SQLite. Intended; see the
			// long note on HostedServiceGate.
			builder.Services.AddGatedHostedService<ChannelIngestionPipeline>();
			builder.Services.AddGatedHostedService<PetBox.Log.Core.Retention.RetentionService>();
			builder.Services.AddGatedHostedService<PetBox.Log.Core.Retention.LogOrphanCleanupService>();
		}
		if (new FeatureFlags(builder.Configuration).IsEnabled(Feature.Dashboard))
		{
			builder.Services.AddHttpClient();
			builder.Services.AddGatedHostedService<PetBox.Dashboard.HealthPoller>();
		}
		if (new FeatureFlags(builder.Configuration).IsEnabled(Feature.Data))
		{
			builder.Services.AddGatedHostedService<PetBox.Data.OrphanCleanupService>();
			builder.Services.AddGatedHostedService<PetBox.Data.WalCheckpointService>();
		}

		// MCP server. Tools are discovered via reflection from the Web assembly
		// (Mcp/*Tools.cs). HTTP transport — agents reach petbox at /mcp with
		// X-Api-Key just like the REST endpoints.
		// Copy the SDK's default tool-serialization options (camelCase, null-ignore, MCP
		// converters) and only relax the text encoder: the default escapes every non-ASCII
		// char (Cyrillic -> \uXXXX), making tool-result JSON unreadable. Allow common ranges
		// (not UnsafeRelaxed) so output is human-readable while HTML-sensitive chars stay escaped.
		var mcpJson = new System.Text.Json.JsonSerializerOptions(ModelContextProtocol.McpJsonUtilities.DefaultOptions)
		{
			Encoder = PetBox.Core.Json.PetBoxJsonEncoder.Relaxed,
		};
		builder.Services.AddMcpServer()
			.WithHttpTransport(o => o.Stateless = true)
			// Schema-honest registration: nullable record properties are NOT marked
			// `required` in the generated in/out schemas, so our null-omitting serializer
			// (WhenWritingNull, incl. the bodyLen contract) stays conformant for strict
			// clients. Replaces WithToolsFromAssembly (which has no schema-options seam).
			.WithSchemaHonestToolsFromAssembly(typeof(Program).Assembly, mcpJson, PetBox.Web.Mcp.McpOutputSchema.NullableAware)
			.WithRequestFilters(filters =>
			{
				PetBox.Web.Mcp.McpErrorEnvelopeFilter.Register(filters); // exceptions -> structured {error} body
				PetBox.Web.Mcp.McpToolScopeFilter.Register(filters);     // A7b: scope-trim tools/list
				PetBox.Web.Mcp.McpTracingFilter.Register(filters);       // span per tool call (self-tracing)
																		 // LAST = INNERMOST (the SDK nests each registered filter inside the previous one), which
																		 // is exactly where the projectKey injection must sit: inside McpTracingFilter, so the
																		 // ambient Activity is the `mcp.tool <name>` span the misroute marker belongs on.
				PetBox.Web.Mcp.McpProjectDefaultFilter.Register(filters); // the key's default project: inject + honest schema
				PetBox.Web.Mcp.McpProjectExistsFilter.Register(filters);  // …and the RESOLVED project must exist (W3)
			});
		builder.Services.AddSingleton<FeatureFlags>();

		// json-encoder-shared-globally: the MCP path already relaxed its own JsonSerializerOptions
		// (mcpJson above), and logs/LLM-prompts/the methodology editor got the same treatment
		// piecemeal — but minimal-API JSON results were never touched, so every NEW minimal-API
		// surface reintroduced the "Cyrillic -> \uXXXX" bug from scratch. `ConfigureHttpJsonOptions`
		// is what a minimal-API endpoint's implicit JSON result (a POCO return, `Results.Json`,
		// `TypedResults.Json`) reads. The Razor Pages/MVC half (`JsonResult`) is wired below, next to
		// the existing `AddRazorPages` call — `.AddJsonOptions` is what `JsonResult`'s executor reads
		// (`SystemTextJsonResultExecutor` falls back to `IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>`
		// whenever `JsonResult.SerializerSettings` is null, which every `new JsonResult(x)` call site
		// in this repo leaves null). Verified empirically, not just by reading the framework source —
		// see BoardSearchIndexEncodingTests (TaskBoard's ?handler=SearchIndex, a real HTTP
		// round-trip) and JsonOptionsWiringTests (an architecture test asserting both IOptions
		// instances carry this encoder, so a future refactor that drops either wire-up fails loudly
		// instead of quietly reopening the hole).
		builder.Services.ConfigureHttpJsonOptions(o =>
			o.SerializerOptions.Encoder = PetBox.Core.Json.PetBoxJsonEncoder.Relaxed);

		// Built-in .NET 10 OpenAPI document generation. The document is materialized at build
		// time into doc/api/PetBox.Web.json (see OpenApiGenerateDocuments in the csproj) and
		// served at runtime only in Development (MapOpenApi in Configure).
		builder.Services.AddOpenApi();

		// static-assets-compression-cache: production served app.css (120,190 B) and site.js
		// (210,643 B) with NO Content-Encoding at all — curl with `Accept-Encoding: gzip, br` got
		// the same uncompressed bytes back. Measured on prod against the light /doc page: DOM
		// phase alone was 3.16 s at ~50 KB/s effective throughput, dwarfing the 25 ms the access
		// log shows the server actually spent rendering. Brotli + Gzip cover every client; Brotli
		// is preferred where both are accepted (the default provider order already ranks it
		// first). EnableForHttps is safe here specifically BECAUSE of the BREACH consideration
		// below — the classic BREACH oracle needs a fixed secret to sit in a compressed response
		// alongside attacker-controlled input reflected across MANY same-secret requests; ASP.NET
		// Core's antiforgery cookie token is stable but the per-response FORM token is randomized
		// every render (double-submit pattern), so there is no fixed compressed secret to
		// distinguish-by-length across requests. HTML compression is the one that matters most in
		// absolute bytes (the tasks board emits megabytes of HTML for 477 nodes).
		builder.Services.AddResponseCompression(options =>
		{
			options.EnableForHttps = true;
			options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
			options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
			options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes.Concat(
			[
				"text/css",
				"application/javascript",
				"text/javascript",
				"image/svg+xml",
				"text/html",
			]);
			// The SSE live-tail stream (/api/logs/{p}/{log}/live-tail, text/event-stream) writes one
			// small chunk per log line and relies on each write reaching the client immediately
			// (LogApi.cs sets `X-Accel-Buffering: no` and flushes right after the headers for the
			// same reason). Response compression wraps the body in a buffering compression stream
			// that only flushes on its own schedule — compressing this response would silently turn
			// a live tail into a stalled-then-bursty one. text/event-stream is also nowhere near the
			// MIME types above, but excluding it explicitly makes the intent survive a future
			// broadening of that list.
			options.ExcludedMimeTypes = ["text/event-stream"];
		});

		builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));
		builder.Services.Configure<ConfigApiKeyOptions>(builder.Configuration.GetSection("Auth"));
		builder.Services.AddSingleton<ConfigApiKeyLookup>();
		builder.Services.AddScoped<DbApiKeyLookup>();
		builder.Services.AddScoped<IApiKeyLookup, CompositeApiKeyLookup>();
		// spec apikey-last-used — the singleton the auth middleware stamps into, and the background
		// flusher that folds the marks into ApiKeys.LastUsedAt in one batched statement (~5 min).
		builder.Services.AddSingleton<IKeyStatService, KeyStatService>();
		builder.Services.AddGatedHostedService<KeyStatFlusher>();

		builder.Logging.Configure(o => o.ActivityTrackingOptions =
			ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

		var selfLogEnabled = builder.Configuration.GetValue("Seq:SelfLog:Enabled", false);
		if (selfLogEnabled && new FeatureFlags(builder.Configuration).IsEnabled(Feature.Logging))
		{
			var staticProps = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, System.Text.Json.JsonElement>();
			staticProps["Env"] = System.Text.Json.JsonSerializer.SerializeToElement(builder.Environment.EnvironmentName);
			staticProps["Ver"] = System.Text.Json.JsonSerializer.SerializeToElement(Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev");
			staticProps["Sha"] = System.Text.Json.JsonSerializer.SerializeToElement(Environment.GetEnvironmentVariable("GIT_SHORT_SHA") ?? "local");
			staticProps["Host"] = System.Text.Json.JsonSerializer.SerializeToElement(Environment.MachineName);

			builder.Services.Configure<PetBox.Log.Core.SelfLogging.SystemLoggerOptions>(
				builder.Configuration.GetSection("SelfLogging"));
			builder.Services.PostConfigure<PetBox.Log.Core.SelfLogging.SystemLoggerOptions>(o =>
				o.StaticProperties = staticProps.ToImmutable());

			builder.Services.AddSingleton<PetBox.Log.Core.SelfLogging.SystemLoggerProvider>();
			builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(
				sp => sp.GetRequiredService<PetBox.Log.Core.SelfLogging.SystemLoggerProvider>());
			builder.Services.AddGatedHostedService<PetBox.Log.Core.SelfLogging.SystemLogFlusher>();
		}

		var otelEnabled = builder.Configuration.GetValue("OpenTelemetry:Enabled", false);
		var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
		if (otelEnabled && !string.IsNullOrWhiteSpace(otlpEndpoint))
		{
			var serviceName = builder.Configuration["OpenTelemetry:ServiceName"] ?? "petbox";
			var serviceVersion = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev";
			var otelApiKey = builder.Configuration["Seq:SelfLog:ApiKey"] ?? string.Empty;

			builder.Services.AddOpenTelemetry()
				.ConfigureResource(r => r.AddService(serviceName, serviceVersion: serviceVersion))
				.WithTracing(tracing => tracing
					.AddAspNetCoreInstrumentation(opts =>
					{
						opts.Filter = ctx =>
							!ctx.Request.Path.StartsWithSegments("/health")
							&& !ctx.Request.Path.StartsWithSegments("/version")
							&& !ctx.Request.Path.StartsWithSegments("/api/events/raw")
							&& !ctx.Request.Path.StartsWithSegments("/v1/logs")
							&& !ctx.Request.Path.StartsWithSegments("/v1/traces");
					})
					// Outgoing HTTP as client spans (LLM providers, embedders) — except the
					// telemetry self-export, which would recurse (spec: trace-outgoing-http).
					.AddHttpClientInstrumentation(opts =>
						opts.FilterHttpRequestMessage = req =>
							PetBox.Web.Observability.SelfTelemetryFilter.ShouldTrace(req.RequestUri))
					// Service-layer sources (self-tracing): without AddSource a source's spans
					// are never sampled, so a request trace stays a single AspNetCore span.
					.AddSource(
						PetBox.Log.Core.Observability.ActivitySources.IngestionSourceName,
						PetBox.Log.Core.Observability.ActivitySources.QuerySourceName,
						PetBox.Log.Core.Observability.ActivitySources.RetentionSourceName,
						PetBox.Core.Observability.PetBoxActivitySources.TasksSourceName,
						PetBox.Core.Observability.PetBoxActivitySources.MemorySourceName,
						PetBox.Core.Observability.PetBoxActivitySources.SearchSourceName,
						PetBox.Core.Observability.PetBoxActivitySources.McpSourceName)
					.AddOtlpExporter(o =>
					{
						o.Endpoint = new Uri(otlpEndpoint);
						o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
						if (!string.IsNullOrWhiteSpace(otelApiKey))
							o.Headers = $"X-Seq-ApiKey={otelApiKey}";
					}));
		}

		// Persist DataProtection keys to the configured directory (the data volume in
		// Docker via DataProtection__KeysDirectory) so the auth cookie survives container
		// restarts / deploys. Without this the default ephemeral keyring regenerates on
		// every restart and logs everyone out. Env unset (local/tests) -> default keyring.
		var dpKeysDir = builder.Configuration["DataProtection:KeysDirectory"];
		if (!string.IsNullOrWhiteSpace(dpKeysDir))
		{
			Directory.CreateDirectory(dpKeysDir);
			builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir));
		}

		builder.Services.AddAuthentication(options =>
		{
			options.DefaultScheme = "Smart";
		})
			.AddPolicyScheme("Smart", "Cookie or API Key", o =>
			{
				o.ForwardDefaultSelector = ctx =>
					ctx.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.ApiKeyHeader)
					|| ctx.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.LegacyApiKeyHeader)
						? ApiKeyAuthenticationHandler.SchemeName
						: CookieAuthenticationDefaults.AuthenticationScheme;
				o.ForwardChallenge = CookieAuthenticationDefaults.AuthenticationScheme;
			})
			.AddCookie(o =>
			{
				o.LoginPath = "/Login";
				// 403 must NOT be dressed up as "you are not signed in": /Login is [AllowAnonymous],
				// so an already-authenticated user forbidden from a page was shown the sign-in form
				// and looped through it forever (auth-denied-and-empty-state). /AccessDenied says so.
				o.AccessDeniedPath = "/AccessDenied";
				o.ExpireTimeSpan = TimeSpan.FromDays(7);
				o.SlidingExpiration = true;
			})
			.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
				ApiKeyAuthenticationHandler.SchemeName, null);

		builder.Services.AddAuthorizationBuilder()
			.AddPolicy("ApiKey", p => p
				.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
				.RequireAuthenticatedUser())
			// For the log routes a browser opens directly: the live-tail SSE stream, and the lazy
			// event-details fetch (Pages/Logs/EventDetails.cshtml.cs) a live row's expand triggers. An
			// EventSource cannot set headers at all, and the fetch call deliberately does not add one
			// either (same-origin cookies already carry the session) — under the header-only "ApiKey"
			// policy every live tail from the UI 401'd. This policy admits the cookie scheme ALONGSIDE
			// the api-key one; it proves only that ONE of them authenticated, and each endpoint then
			// authorizes the principal on its own terms via LogApi.AuthorizeProjectViewerAsync (shared by
			// both): an api key by project claim + logs:query scope, a session by workspace role — a
			// cookie has no scopes and an api key has no roles, so the two gates never substitute for one
			// another). Do NOT reach for this policy to "fix a 401" on any other route: a token in a URL
			// was rejected for live-tail precisely because it leaks into access logs, and a cookie on a
			// header-only API is a CSRF surface everywhere it is not a read-only stream.
			.AddPolicy("ApiKeyOrCookie", p => p
				.AddAuthenticationSchemes(
					ApiKeyAuthenticationHandler.SchemeName,
					CookieAuthenticationDefaults.AuthenticationScheme)
				.RequireAuthenticatedUser())
			.AddPolicy("ConfigRead", p =>
			{
				p.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName);
				p.AddRequirements(new ScopeRequirement(ApiKeyScopes.ConfigRead));
			})
			.AddPolicy("ConfigWrite", p =>
			{
				p.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName);
				p.AddRequirements(new ScopeRequirement(ApiKeyScopes.ConfigWrite));
			})
			.AddPolicy("DataRead", p =>
			{
				p.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName);
				p.AddRequirements(new ScopeRequirement(ApiKeyScopes.DataRead));
			})
			.AddPolicy("DataWrite", p =>
			{
				p.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName);
				p.AddRequirements(new ScopeRequirement(ApiKeyScopes.DataWrite));
			})
			.AddPolicy("DataSchema", p =>
			{
				p.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName);
				p.AddRequirements(new ScopeRequirement(ApiKeyScopes.DataSchema));
			})
			.AddPolicy("LlmInvoke", p =>
			{
				p.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName);
				p.AddRequirements(new ScopeRequirement(ApiKeyScopes.LlmInvoke));
			})
			.AddPolicy("WorkspaceAdmin", p =>
			{
				p.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme);
				p.RequireAuthenticatedUser();
				p.AddRequirements(new WorkspaceRoleRequirement(WorkspaceRole.Admin));
			})
			.AddPolicy("WorkspaceMember", p =>
			{
				p.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme);
				p.RequireAuthenticatedUser();
				p.AddRequirements(new WorkspaceRoleRequirement(WorkspaceRole.Member));
			})
			.AddPolicy("SysAdmin", p =>
			{
				p.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme);
				p.RequireAuthenticatedUser();
				p.RequireClaim(PetBoxClaims.IsSysAdmin, "true");
			})
			.AddPolicy("WorkspaceViewer", p =>
			{
				p.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme);
				p.RequireAuthenticatedUser();
				p.AddRequirements(new WorkspaceRoleRequirement(WorkspaceRole.Viewer));
			})
			// Self-service workspace creation: the account's explicit numeric quota still exceeds the
			// number of workspaces it owns (spec workspace-create-permission). Evaluated against the DB
			// per ask — see WorkspaceCreateAuthorizationHandler for why it is not a claim.
			.AddPolicy("CanCreateWorkspace", p =>
			{
				p.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme);
				p.RequireAuthenticatedUser();
				p.AddRequirements(new WorkspaceCreateRequirement());
			});
		builder.Services.AddHttpContextAccessor();
		builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, ScopeAuthorizationHandler>();
		builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, WorkspaceRoleAuthorizationHandler>();
		// Scoped, not singleton: it opens a core-db connection per evaluation (ICoreDbFactory is the
		// only way in — no DataConnection is injectable).
		builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, WorkspaceCreateAuthorizationHandler>();
		// Rebuilds the workspace-membership claims from the DB on every cookie-authenticated
		// request, so an added/removed membership takes effect without a re-login.
		builder.Services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation,
			PetBox.Web.Auth.WorkspaceClaimsRefresher>();
		builder.Services.AddScoped<INavigationContext, NavigationContext>();
		builder.Services.AddScoped<PetBox.Web.Search.CrossScopeTaskSearchService>();
		// The DB half of settings (the Settings table + the scope cascade) and the TYPED half
		// (reflection over [Setting], encode/decode). SettingsResolver holds no factory: it asks the
		// store. Both scoped — the store takes a fresh call-owned connection per call, exactly like
		// every other core.db service.
		builder.Services.AddScoped<PetBox.Core.Settings.ISettingsStore, PetBox.Core.Settings.SettingsStore>();
		builder.Services.AddScoped<PetBox.Core.Settings.ISettingsResolver, PetBox.Web.Settings.SettingsResolver>();
		// Typed, memoized-per-request view accessor over UiStateResolver — see UiState.cs. Scoped so
		// the memoization field lives exactly one request; never resolved eagerly in middleware.
		builder.Services.AddScoped<PetBox.Web.Settings.IUiState, PetBox.Web.Settings.UiState>();
		// Server-side markdown renderer for read surfaces (reader-view detectability). Singleton:
		// the Markdig pipeline + HtmlSanitizer are built once and are thread-safe to reuse.
		builder.Services.AddSingleton<PetBox.Web.Rendering.IMarkdownRenderer, PetBox.Web.Rendering.MarkdownRenderer>();
		// Loads the public /doc page bodies from their markdown canon (Pages/Doc/content/*.md),
		// the single source those pages render through the shared renderer. Stateless — singleton.
		builder.Services.AddSingleton<PetBox.Web.Pages.Doc.DocContent>();
		builder.Services.AddScoped<PetBox.Web.Auth.ProjectWorkspaceBindingFilter>();
		builder.Services.AddScoped<PetBox.Web.Auth.AgentKeyAdminService>();
		// core.db lives in the service layer only: the binding filter and the claims transformation
		// above ask these, they never open a connection themselves (db-access-layer-cleanup).
		// SINGLETON, deliberately: it carries the project cache (db-cache-behind-services), and a
		// cache inside a scoped service would be per-request and buy nothing. Its dependencies
		// (ICoreDbFactory, IMemoryCache) are singletons — CaptiveDependencyTests guards this wiring.
		builder.Services.AddSingleton<PetBox.Web.Auth.IProjectDirectory, PetBox.Web.Auth.ProjectDirectory>();
		// The membership + account services live in PetBox.Core, not here: AdminBootstrapper and
		// WorkspaceProvisioning are Core writers of WorkspaceMembers and must be able to reach them.
		builder.Services.AddScoped<PetBox.Core.Auth.IWorkspaceMembershipService, PetBox.Core.Auth.WorkspaceMembershipService>();
		builder.Services.AddScoped<PetBox.Core.Auth.IUserAdminService, PetBox.Core.Auth.UserAdminService>();
		// The two NARROW auth doors. Neither is IUserAdminService, and that is the whole point: the
		// login page is anonymous and /Me/Security is reachable by every logged-in user, while the admin
		// service can reset ANY account's password. ICredentialAuthenticator can only CHECK a password
		// it was handed (and owns the bootstrap-admin lockdown rule); IAccountSelfService can only
		// change the password of the account the request is authenticated as — it takes no user id.
		builder.Services.AddScoped<PetBox.Core.Auth.ICredentialAuthenticator, PetBox.Core.Auth.CredentialAuthenticator>();
		builder.Services.AddScoped<PetBox.Core.Auth.IAccountSelfService, PetBox.Core.Auth.AccountSelfService>();
		builder.Services.AddScoped<PetBox.Web.Auth.IWorkspaceAdminService, PetBox.Web.Auth.WorkspaceAdminService>();
		// The doors the MCP tools ask instead of opening core.db themselves (db-access-layer-cleanup):
		// the DataDbs catalog (db_* tools), the HealthReports reader (health_search) and the workspace
		// memory containers (memory_* tools — resolve, lazily ensure, and the reachability predicate).
		builder.Services.AddScoped<PetBox.Data.Contract.IDataDbCatalog, PetBox.Data.Services.DataDbCatalog>();
		builder.Services.AddScoped<PetBox.Core.Health.IHealthReportService, PetBox.Core.Health.HealthReportService>();
		// The COUNTS door: Admin/Dashboard/ProjectHome landing pages ask this instead of opening
		// core.db themselves (db-out-of-pages-remaining-24 group B). One connection per page's whole
		// rollup — the thing that finally makes core.db cacheable (db-cache-behind-services).
		builder.Services.AddScoped<PetBox.Core.Data.ICoreDbRollupService, PetBox.Core.Data.CoreDbRollupService>();
		// The pull-mode endpoint list — a DIFFERENT table from HealthReports above, and one that had no
		// door at all until Pages/Admin/ProjectDetail stopped opening core.db for it. Same namespace as
		// the reports door on purpose: a caller who finds one finds the other (commit ce12100).
		builder.Services.AddScoped<PetBox.Core.Health.IHealthEndpointDirectory, PetBox.Core.Health.HealthEndpointDirectory>();
		builder.Services.AddScoped<PetBox.Core.Data.IWorkspaceMemoryDirectory, PetBox.Core.Data.WorkspaceMemoryDirectory>();
		// The share-token door: ShareLinks had no owner (Pages.ShareModel's anonymous resolve page and
		// PetBox.Log.Core.ShareApi's create/TSV endpoints both opened core.db directly for it).
		builder.Services.AddScoped<PetBox.Core.Data.IShareLinkDirectory, PetBox.Core.Data.ShareLinkDirectory>();
		builder.Services.AddRazorPages(options =>
		{
			// Project-scoped Config — same Config/Index page, applies project:{projectKey} filter.
			options.Conventions.AddPageRoute("/Config/Index", "/ui/{workspaceKey}/{projectKey}/config");
			options.Conventions.AddPageRoute("/Config/Editor", "/ui/{workspaceKey}/{projectKey}/config/editor/{bindingId:long?}");
			options.Conventions.AddPageRoute("/Config/History", "/ui/{workspaceKey}/{projectKey}/config/history");
			options.Conventions.AddPageRoute("/Config/Preview", "/ui/{workspaceKey}/{projectKey}/config/preview");
			// Canonical human-readable node address /tasks/{board}/{slug} (node-slug-addressable).
			// Same page as the opaque /tasks/node/{nodeId} alias (its own @page); the literal
			// "node" segment outranks {board}, and `node` is a reserved board name, so they never
			// collide. The page model binds either (board, slug) or nodeId.
			options.Conventions.AddPageRoute("/ProjectHome/TaskBoardNode", "/ui/{workspaceKey}/{projectKey}/tasks/{board}/{slug}");

			// Global gate for the whole {workspaceKey}/{projectKey} IDOR class (workspace-access-
			// isolation follow-up, same-class-cross-tenant-field-id-4c0359): a Workspace* policy only
			// proves membership of the route WORKSPACE, never that the route PROJECT lives in it. One
			// filter on every page beats a per-page `p.WorkspaceKey == WorkspaceKey` check that a new
			// page can forget to copy.
			options.Conventions.ConfigureFilter(
				new Microsoft.AspNetCore.Mvc.ServiceFilterAttribute(typeof(PetBox.Web.Auth.ProjectWorkspaceBindingFilter)));
		})
			// json-encoder-shared-globally: see the ConfigureHttpJsonOptions comment above — this is
			// the other half, read by JsonResult's executor (IOptions<Mvc.JsonOptions>) whenever a
			// `new JsonResult(x)` call site (e.g. TaskBoard's ?handler=SearchIndex, Config's reveal
			// endpoint) leaves SerializerSettings null, which is every call site in this repo today.
			.AddJsonOptions(o => o.JsonSerializerOptions.Encoder = PetBox.Core.Json.PetBoxJsonEncoder.Relaxed);
	}

	// Path prefixes for programmatic (non-browser) surfaces that must keep their raw status
	// codes + JSON/empty bodies rather than the HTML /Error re-execute. Ordinal, case-insensitive.
	static readonly string[] ProgrammaticPathPrefixes =
		["/api", "/mcp", "/v1", "/health", "/version", "/agent", "/openapi", "/.well-known"];

	static bool IsProgrammaticPath(PathString path)
	{
		foreach (var prefix in ProgrammaticPathPrefixes)
			if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}

	public static void Configure(WebApplication app)
	{
		// Resolve via Services (not app.Configuration directly) — under
		// WebApplicationFactory the IConfiguration captured by Services has
		// the full pipeline of providers including the test-override
		// ConfigureAppConfiguration callbacks, which app.Configuration here
		// may not yet reflect when the factory composes the host.
		var configuration = app.Services.GetRequiredService<IConfiguration>();
		var connectionString = configuration.GetConnectionString("PetBox")
			?? "Data Source=./data/petbox.db;Cache=Shared";

		// Snapshot existing dbs before applying migrations — best-effort, never block
		// startup on a backup failure (the periodic BackupService covers later passes).
		var dataDir = Path.GetDirectoryName(
			new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource)!;
		try
		{
			PetBox.Core.Data.Backup.SnapshotAll(
				dataDir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"), "pre-migration", PetBox.Core.Data.BackupService.RetainSets);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"PetBox pre-migration backup failed: {ex.Message}");
		}

		MigrationRunner.Run(connectionString);

		// Deploy: single fleet-wide db (data/deploy.db). Ensure its schema once at
		// startup (the per-project stores ensure lazily via their factories instead).
		PetBox.Deploy.Data.DeploySchema.Ensure($"Data Source={Path.Combine(dataDir, "deploy.db")};Cache=Shared");

		// One-time, idempotent: fold legacy per-board task files (tasks/<proj>/<board>.db)
		// into the per-project file (tasks/<proj>.db). Keeps originals (renamed .migrated)
		// and reconciles row counts; the pre-migration snapshot above is the safety net.
		var tasksFactory = app.Services.GetRequiredService<IScopedDbFactory<PetBox.Tasks.Data.TasksDb>>();
		var tasksMigLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tasks.LegacyMigration");
		// Returns the count migrated; the migrator logs each board itself, so ignore it here.
		new PetBox.Tasks.Data.LegacyTaskFileMigrator(Path.Combine(dataDir, "tasks"), tasksFactory, tasksMigLog).Migrate();

		// One-time, idempotent (relations-in-project-db): move typed edges out of the Core DB's
		// legacy Relation table into each project's own tasks file, where their endpoints get a
		// real FK to the nodes. Dangling edges (endpoint node gone) are DROPPED and logged. Runs
		// after the legacy fold (so every board is in its per-project file) and BEFORE the flat
		// back-fill below — that one creates part_of edges through the store, i.e. already into
		// the new home, so the copy has to land first or the same edge would exist twice.
		var coreDbFactory = app.Services.GetRequiredService<ICoreDbFactory>();

		{
			var relLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tasks.RelationsBackfill");
			new PetBox.Tasks.Data.RelationsToTasksDbMigrator(coreDbFactory, tasksFactory, Path.Combine(dataDir, "tasks"), relLog).Migrate();
		}

		// One-time, idempotent (spec-flat-tags): convert legacy path-keyed nodes to flat
		// slugs + synthesize part_of edges. Runs after the legacy fold so every board is in
		// its per-project file; the part_of edges land in the per-project tasks file.
		using (var flatScope = app.Services.CreateScope())
		{
			var relations = new PetBox.Tasks.Data.RelationStore(tasksFactory);
			var flatLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tasks.FlatNodeMigration");
			new PetBox.Tasks.Data.FlatNodePartOfMigrator(Path.Combine(dataDir, "tasks"), tasksFactory, relations, flatLog).Migrate();
		}

		// One-time, idempotent (methodology-instance-backfill): every existing TaskBoard gets
		// exactly-one MethodologyInstance membership. Creates methodology_instances rows from
		// project def / effective builtins; packs quartet process-role boards into one shared
		// instance when possible. Runs after schema ensure + flat migration; needs core.db
		// (membership) + per-project tasks files (instance documents).
		{
			var backfillLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tasks.MethodologyInstanceBackfill");
			new PetBox.Tasks.Data.MethodologyInstanceBackfill(coreDbFactory, tasksFactory, backfillLog).Migrate();
		}

		// One-time, idempotent (work-preset-drop-deferred): the `work` kind's builtin preset
		// no longer declares the `Deferred` status. A preset code change alone does not reach
		// a definition/instance already materialized (verbatim-copied) into a project's stored
		// methodology document before this change — this strips `Deferred` (status +
		// referencing transitions) from every stored document that still carries it. Runs
		// after the instance backfill so every project's documents are in their per-project
		// tasks file already.
		{
			var deferredLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tasks.WorkDeferredStatusMigrator");
			new PetBox.Tasks.Data.WorkDeferredStatusMigrator(coreDbFactory, tasksFactory, deferredLog).Migrate();
		}

		// One-time, idempotent (llm-registry-own-store): copy the live LLM registry out of the Config
		// module (config/$system.db: the `llm/registry` JSON binding + one `llm/secret/{endpoint}`
		// binding per api key) into core.db's llm_endpoints/llm_routes at level System:$. A startup
		// hook and NOT a migration on purpose — the source rows live in a DIFFERENT FILE whose path is
		// runtime config, which a core.db migration could only reach by ATTACHing raw SQL and would
		// fail wherever that file does not exist. Api keys are copied as CIPHERTEXT, verbatim (same
		// AES-GCM, same master key): never decrypted, so the import needs no PETBOX_MASTER_KEY and no
		// plaintext key ever exists. The old bindings are NOT touched — they stay read-only and keep
		// serving the router until the DI flip lands.
		using (var llmScope = app.Services.CreateScope())
		{
			var coreFactory = llmScope.ServiceProvider.GetRequiredService<ICoreDbFactory>();
			var configFactory = llmScope.ServiceProvider.GetRequiredService<IConfigDbFactory>();
			var llmImportLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LlmRouter.RegistryImport");
			new PetBox.LlmRouter.Registry.LlmRegistryImporter(
				coreFactory, configFactory, Path.Combine(dataDir, "config"), llmImportLog).Import();
		}

		using (var scope = app.Services.CreateScope())
		{
			// The first-boot admin seed through its service door: startup code is not the service
			// layer and does not open core.db itself (AGENTS.md). The seed itself is still
			// AdminBootstrapper's, on its own connection — see IUserAdminService.EnsureBootstrapAdminAsync.
			scope.ServiceProvider.GetRequiredService<PetBox.Core.Auth.IUserAdminService>()
				.EnsureBootstrapAdminAsync().GetAwaiter().GetResult();

			// The petbox self-log is the one log created automatically — petbox's
			// own ILogger + Seq self-log write here. User logs are created explicitly.
			if (new FeatureFlags(configuration).IsEnabled(Feature.Logging))
			{
				var logStore = scope.ServiceProvider.GetRequiredService<ILogStore>();
				if (!logStore.ExistsAsync(LogNames.SystemProject, LogNames.SelfLog).GetAwaiter().GetResult())
					logStore.CreateAsync(LogNames.SystemProject, LogNames.SelfLog, "PetBox self-log")
						.GetAwaiter().GetResult();

				// The access-line log (self-telemetry-log-routing): the default SelfLogging routing
				// rule sends RequestLoggingMiddleware's EventId 500-503 here instead of `petbox`. Log
				// creation forbids public ingest auto-vivifying a log (log-create-before-ingest), so
				// this must exist before the first request lands, same as `petbox` above. 14-day
				// window is the owner's call — shorter than the system default because access lines
				// are high-volume and low-value past ~2 weeks. Idempotent: an operator-adjusted
				// retention on an existing `access` log is left alone across restarts — only the
				// ExistsAsync gate decides whether to create, CreateAsync is never called on a log
				// that already exists.
				if (!logStore.ExistsAsync(LogNames.SystemProject, LogNames.AccessLog).GetAwaiter().GetResult())
					logStore.CreateAsync(LogNames.SystemProject, LogNames.AccessLog, "PetBox self-log: access", retentionDays: 14)
						.GetAwaiter().GetResult();
			}
		}

		// First middleware: apply X-Forwarded-* from the reverse proxy (Caddy terminates TLS),
		// so Request.Scheme/Host/RemoteIp reflect the public request. Without this, absolute
		// URLs the app emits (e.g. node permalinks via tasks include_url) come out as the
		// internal http hop. Options configured in ConfigureServices (trusts the proxy).
		app.UseForwardedHeaders();

		if (!app.Environment.IsDevelopment())
		{
			app.UseExceptionHandler("/Error");
			app.UseHsts();
		}

		// Friendly custom error page for bare 4xx/5xx responses that carry no body — most
		// importantly a 404 for an unknown path or an unknown/non-member workspace key (the
		// /ui/{workspaceKey} catch-all now returns NotFound instead of masquerading as $system).
		// Re-executes the /Error Razor page preserving the original status code (passed as ?code).
		app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");

		// Programmatic surfaces (REST/MCP/telemetry/health) must keep returning their raw status
		// codes — JSON problem bodies already opt out (they set a ContentType, which the
		// status-code-pages middleware skips), but bare codes (401/403/500) would otherwise be
		// re-executed into an HTML page. Disable the re-execute for those path prefixes so only
		// browser routes get the friendly page.
		app.Use(async (ctx, next) =>
		{
			if (IsProgrammaticPath(ctx.Request.Path)
				&& ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } scp)
				scp.Enabled = false;
			await next();
		});

		// Must run BEFORE UseStaticFiles so app.css/site.js get compressed too, and before
		// UseRouting/endpoint dispatch so it also wraps the HTML/JSON responses further down the
		// pipeline. text/event-stream (SSE live-tail) is excluded above at registration time.
		app.UseResponseCompression();

		app.UseStaticFiles(new StaticFileOptions
		{
			OnPrepareResponse = ctx =>
			{
				// _Layout.cshtml / _PublicLayout.cshtml reference app.css/site.js with
				// asp-append-version="true", which stamps a content-hash query string (?v=...): a
				// changed file gets a new URL, so it is safe to tell the browser to cache it forever.
				// An asset requested WITHOUT ?v= carries no such guarantee (a direct/bookmarked URL,
				// or any static file that isn't versioned) — marking that immutable for a year would
				// pin a stale copy across a deploy that changes the file's bytes at the same URL. So
				// only the versioned request gets the year-long immutable cache; everything else gets
				// a short max-age that still avoids a revalidation round-trip within one session
				// without risking staleness across a deploy.
				var isVersioned = ctx.Context.Request.Query.ContainsKey("v");
				ctx.Context.Response.Headers.CacheControl = isVersioned
					? "public, max-age=31536000, immutable"
					: "public, max-age=600";
			},
		});
		app.UseRouting();
		app.UseAuthentication();

		// spec apikey-last-used: record the key's use IN MEMORY (KeyStatService), never in SQLite —
		// the auth hot path stays a single indexed read. KeyStatFlusher persists the batch every
		// ~5 min. Sits above UseAuthorization so a call refused for a missing SCOPE still counts as
		// a use of the key (it authenticated); a bogus key does not.
		app.UseMiddleware<PetBox.Core.Auth.KeyUsageStampMiddleware>();

		app.UseAuthorization();

		// App-wide request logging into the self-log (after auth so the project claim is
		// available; below UseExceptionHandler so it logs+rethrows unhandled exceptions).
		app.UseMiddleware<PetBox.Web.Logging.RequestLoggingMiddleware>();

		// Persist URL-driven workspace switch into the yb_ws cookie. Without
		// this, visiting /ui/{ws}/... shows that workspace for one render but
		// subsequent pages without an explicit workspaceKey fall back to the
		// stale cookie value.
		// Membership validation happens downstream in NavigationContext.ResolveWorkspace,
		// so it's safe to write the cookie unconditionally when route has the key.
		app.Use(async (ctx, next) =>
		{
			var routeWs = ctx.GetRouteValue("workspaceKey")?.ToString();
			if (!string.IsNullOrEmpty(routeWs) && ctx.User.Identity?.IsAuthenticated == true)
			{
				var cookie = ctx.Request.Cookies[WorkspaceSwitchEndpoint.CookieName];
				if (!string.Equals(cookie, routeWs, StringComparison.Ordinal))
				{
					ctx.Response.Cookies.Append(WorkspaceSwitchEndpoint.CookieName, routeWs, new CookieOptions
					{
						HttpOnly = false,
						SameSite = SameSiteMode.Lax,
						Expires = DateTimeOffset.UtcNow.AddDays(365),
						IsEssential = true,
						Path = "/",
					});
				}
			}
			await next();
		});

		// Same URL→cookie persistence for the active project. Visiting a project page
		// (/ui/{ws}/{key}/... or the admin project pages — both carry a `projectKey`
		// route value) refreshes yb_project so the sidebar selector stays in sync when
		// the next page has no project in its URL (e.g. workspace Status).
		// Membership/ownership is validated downstream in NavigationContext + the switch
		// endpoint, so an unconditional write on a routed projectKey is safe.
		app.Use(async (ctx, next) =>
		{
			var routeProject = ctx.GetRouteValue("projectKey")?.ToString();
			if (!string.IsNullOrEmpty(routeProject) && ctx.User.Identity?.IsAuthenticated == true)
			{
				var cookie = ctx.Request.Cookies[ProjectSwitchEndpoint.CookieName];
				if (!string.Equals(cookie, routeProject, StringComparison.Ordinal))
				{
					ctx.Response.Cookies.Append(ProjectSwitchEndpoint.CookieName, routeProject, new CookieOptions
					{
						HttpOnly = false,
						SameSite = SameSiteMode.Lax,
						Expires = DateTimeOffset.UtcNow.AddDays(365),
						IsEssential = true,
						Path = "/",
					});
				}
			}
			await next();
		});

		// Serve the OpenAPI document (/openapi/v1.json) in EVERY environment, including
		// Production (bug openapi-not-served-in-prod). Two ways existed to fix the prod gap:
		// (a) lift the IsDevelopment gate so the live endpoint always matches the running code, or
		// (b) serve the build-time-generated doc/api/PetBox.Web.json as a static file. Went with
		// (a): the committed JSON is a build artifact that can silently drift from the deployed
		// binary (a merge that forgets to regenerate it, a hotfix that skips the build step), while
		// MapOpenApi() reflects the actual endpoint metadata of the app that is serving the request
		// — it cannot lie. Anonymous, matching /health and /version below (and /doc's Razor pages):
		// the document only describes the API SURFACE (routes/shapes), which the routing table
		// already discloses to anyone probing it; every endpoint it lists still gates on its own
		// auth policy (ApiKey/Cookie/etc.) unchanged by this. This is a deliberate decision, not an
		// oversight — languages with no client SDK need a URL to fetch the schema from with zero
		// setup (see doc/agent-content overview.md "No SDK for your language").
		app.MapOpenApi().AllowAnonymous();

		app.MapMethods("/health", ["GET", "HEAD"], () => TypedResults.Ok(new HealthStatusResponse("healthy")))
			.Produces<HealthStatusResponse>()
			.AllowAnonymous();

		app.MapMethods("/version", ["GET", "HEAD"], () => TypedResults.Ok(new VersionResponse(
			Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev",
			Environment.GetEnvironmentVariable("GIT_SHORT_SHA") ?? "local",
			Environment.GetEnvironmentVariable("GIT_COMMIT_DATE") ?? string.Empty)))
			.Produces<VersionResponse>()
			.AllowAnonymous();

		// MCP OAuth discovery probes — RELIABILITY FIX (bug mcp-oauth-discovery-html-404).
		// Claude Code (and other MCP SDK clients) register an OAuth auth provider for EVERY http MCP
		// server (`hasAuthProvider:true` in the transport). PetBox authenticates /mcp with an API key
		// (X-Api-Key), NOT OAuth, so it publishes no OAuth metadata. But on a 401 — e.g. a reconnect
		// after a server restart — the client PROBES these RFC 9728 / RFC 8414 well-known paths. With
		// no handler they fell through to the SPA's HTML 404 page; the client then does
		// `JSON.parse('<!DOCTYPE html>…')` and throws
		//   "SDK auth failed: HTTP 404: Invalid OAuth error response: Unrecognized token '<'"
		// which aborts the WHOLE reconnect (the symptom users saw as a dead /mcp). Answer the probes
		// with a clean JSON 404 (application/json) so discovery fails GRACEFULLY and the client falls
		// back to the X-Api-Key header the transport always sends — a fresh connect already works that
		// way. Anonymous (discovery precedes auth); exact + catch-all (spec appends the resource path,
		// e.g. /.well-known/oauth-protected-resource/mcp).
		var oauthDiscoveryBody = new
		{
			error = "not_found",
			error_description = "PetBox authenticates /mcp with an API key (X-Api-Key header); no OAuth authorization is published.",
		};
		IResult OAuthDiscoveryProbe() => Results.Json(oauthDiscoveryBody, statusCode: StatusCodes.Status404NotFound);
		foreach (var wk in new[] { "oauth-protected-resource", "oauth-authorization-server", "openid-configuration" })
		{
			app.MapGet($"/.well-known/{wk}", OAuthDiscoveryProbe).AllowAnonymous();
			app.MapGet($"/.well-known/{wk}/{{*rest}}", OAuthDiscoveryProbe).AllowAnonymous();
		}

		app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
		{
			await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
			return Results.Redirect("/Login");
		});

		app.MapAuthEndpoints();
		app.MapWorkspaceSwitch();
		app.MapProjectSwitch();
		PetBox.Web.Settings.BoardFilterPrefsEndpoint.MapBoardFilterPrefs(app);
		app.MapRazorPages();

		if (new FeatureFlags(app.Configuration).IsEnabled(Feature.Config))
		{
			app.MapConfigEndpoints();
		}

		if (new FeatureFlags(app.Configuration).IsEnabled(Feature.Logging))
		{
			app.MapLogEndpoints();
			app.MapShareEndpoints();
			// OTLP receivers: project-scoped (/v1/{logs,traces}/{project}/{log}) + bare
			// self-export paths (/v1/{logs,traces}) that feed the $system self-log.
			PetBox.Web.Ingestion.OtlpEndpoints.MapOtlpEndpoints(app);

			if (app.Configuration.GetValue("Seq:SelfLog:Enabled", false))
				app.MapSeqSelfLogEndpoint();
		}

		if (new FeatureFlags(app.Configuration).IsEnabled(Feature.Tasks))
			PetBox.Web.Sessions.SessionApi.MapSessionEndpoints(app);

		// Portable agent-definition store (Core DB; always on — no feature flag).
		PetBox.Web.AgentDefs.AgentDefsApi.MapAgentDefsEndpoints(app);

		// Agent memory canon read surface (the wiring hooks pull it at session start).
		if (new FeatureFlags(app.Configuration).IsEnabled(Feature.Memory))
			PetBox.Web.Memory.MemoryApi.MapMemoryEndpoints(app);

		if (new FeatureFlags(app.Configuration).IsEnabled(Feature.LlmRouter))
			PetBox.Web.LlmRouter.LlmRouterApi.MapLlmRouterEndpoints(app);

		if (new FeatureFlags(app.Configuration).IsEnabled(Feature.Dashboard))
		{
			app.MapHealthEndpoints();
		}

		if (new FeatureFlags(app.Configuration).IsEnabled(Feature.Data))
		{
			app.MapDataDbsEndpoints();
			app.MapSchemaEndpoints();
			app.MapQueryExecEndpoints();
		}

		// Deploy control-plane: agent pull contract (/agent/*) + node onboarding
		// (/api/deploy/nodes). Gated by Feature.Deploy (default on) like the other modules.
		if (new FeatureFlags(app.Configuration).IsEnabled(Feature.Deploy))
			PetBox.Web.Deploy.DeployApi.MapDeployEndpoints(app);

		// MCP HTTP endpoint. Tools auto-registered via reflection in builder.Services
		// (see AddMcpServer above). The endpoint goes through the same ApiKey
		// authentication scheme as /api/data/*, so X-Api-Key works identically.
		app.MapMcp("/mcp").RequireAuthorization(p =>
		{
			p.AddAuthenticationSchemes(PetBox.Core.Auth.ApiKeyAuthenticationHandler.SchemeName);
			p.RequireAuthenticatedUser();
		});
	}
}
