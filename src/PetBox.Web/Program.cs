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

		builder.Services.AddScoped(sp => new PetBoxDb(PetBoxDb.CreateOptions(ResolveCs(sp))));
		builder.Services.AddSingleton<IScopedDbFactory<LogDb>>(sp => new ScopedDbFactory<LogDb>(
				Path.Combine(ResolveDataDir(sp), "logs"), PetBox.Core.Settings.Scope.Project,
				cs => new LogDb(LogDb.CreateOptions(cs)), LogSchema.Ensure));
		builder.Services.AddScoped<ILogStore, LogStore>();
		builder.Services.AddScoped<PetBox.Log.Core.Query.ILogQueryService, PetBox.Log.Core.Query.LogQueryService>();
		builder.Services.AddSingleton<IScopedDbFactory<ConfigDb>>(sp => new ScopedDbFactory<ConfigDb>(
				Path.Combine(ResolveDataDir(sp), "config"), PetBox.Core.Settings.Scope.Workspace,
				cs => new ConfigDb(ConfigDb.CreateOptions(cs)), ConfigSchema.Ensure));
		builder.Services.AddSingleton<IConfigDbFactory>(sp => new ConfigDbFactory(sp.GetRequiredService<IScopedDbFactory<ConfigDb>>()));
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
		builder.Services.AddScoped<PetBox.Memory.Contract.IMemoryService, PetBox.Memory.Services.MemoryService>();
		// Usage telemetry intake (spec: memory-usage-observability): singleton queue+drain;
		// called ONLY by the MCP/UI adapters, so internal machine traffic never counts.
		builder.Services.AddSingleton<PetBox.Memory.Contract.IMemoryUsageRecorder, PetBox.Memory.Services.MemoryUsageRecorder>();
		// Background materialization of Class-B (vector) search indexes — the entity write path
		// never blocks on embedding; this drains the temporal log into vectors out-of-band. Each
		// module contributes an IVectorizationJob; memory's is registered here, tasks' in the
		// follow-up. Unconditional like the stores it serves.
		builder.Services.AddScoped<PetBox.Web.Search.IVectorizationJob, PetBox.Web.Search.MemoryVectorizationJob>();
		builder.Services.AddScoped<PetBox.Web.Search.IVectorizationJob, PetBox.Web.Search.TasksVectorizationJob>();
		builder.Services.AddHostedService<PetBox.Web.Search.SearchVectorizationService>();
		// LLM router: neutral ILlmClient (embed/rerank/chat) + ILlmRegistryAdmin over a
		// config-stored endpoint/route registry. Unconditional DI; Feature.LlmRouter gates
		// the MCP surface, not registration.
		builder.Services.AddLlmRouter();
		builder.Services.AddSingleton<IScopedDbFactory<PetBox.Sessions.Data.SessionsDb>>(sp => new ScopedDbFactory<PetBox.Sessions.Data.SessionsDb>(
				Path.Combine(ResolveDataDir(sp), "sessions"), PetBox.Core.Settings.Scope.Project,
				cs => new PetBox.Sessions.Data.SessionsDb(PetBox.Sessions.Data.SessionsDb.CreateOptions(cs)), PetBox.Sessions.Data.SessionsSchema.Ensure));
		builder.Services.AddScoped<PetBox.Sessions.Data.ISessionStore, PetBox.Sessions.Data.SessionStore>();
		builder.Services.AddScoped<PetBox.Sessions.Contract.ISessionService, PetBox.Sessions.Services.SessionService>();
		// Session discovery digests: distills each session's transcript into the project's
		// `session-digests` memory store off the write path — rides the same enrichment tick
		// as the vector jobs. Registered after sessions/memory/llm, which it consumes.
		builder.Services.AddScoped<PetBox.Web.Search.IVectorizationJob, PetBox.Web.Search.SessionDigestJob>();
		// Autocapture: distills durable typed facts from settled sessions into the
		// quarantined `autocaptured` memory store (dedup via hybrid neighbors + LLM judge).
		builder.Services.AddScoped<PetBox.Web.Search.IVectorizationJob, PetBox.Web.Search.SessionFactsJob>();
		// Cross-session behavior-pattern mining over the accumulated distillates —
		// registered AFTER the facts job so a tick mines the freshest observations.
		builder.Services.AddScoped<PetBox.Web.Search.IVectorizationJob, PetBox.Web.Search.BehaviorPatternJob>();
		// Two-stage session search: digest discovery (memory) → episodic hydration.
		builder.Services.AddScoped<PetBox.Web.Search.SessionSearchService>();
		// Episodic tier: transient per-session DuckDB index, hydrated on demand and aged
		// out by idleness. Singleton — it IS the hydration cache.
		builder.Services.AddSingleton<PetBox.Sessions.Contract.ISessionEpisodicIndex>(sp =>
			new PetBox.Sessions.Episodic.DuckDbSessionEpisodicIndex(
				sp.GetRequiredService<IScopedDbFactory<PetBox.Sessions.Data.SessionsDb>>(),
				sp.GetService<PetBox.LlmRouter.Contract.ILlmClient>(),
				sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PetBox.Sessions.Episodic.DuckDbSessionEpisodicIndex>>()));
		// Deploy: single FLEET-WIDE mutable db (one node hosts containers from many
		// projects, so NOT per-project scoped). Schema ensured once at startup in Configure().
		builder.Services.AddScoped(sp => new PetBox.Deploy.Data.DeployDb(
			PetBox.Deploy.Data.DeployDb.CreateOptions($"Data Source={Path.Combine(ResolveDataDir(sp), "deploy.db")};Cache=Shared")));
		builder.Services.AddScoped<PetBox.Deploy.Contract.IDeployService, PetBox.Deploy.Services.DeployService>();
		if (new FeatureFlags(builder.Configuration).IsEnabled(Feature.Deploy))
			builder.Services.AddHostedService<PetBox.Deploy.Services.DeployFailoverSweeper>();
		// Periodic VACUUM INTO snapshots of every internal db; unconditional (data
		// safety is cross-cutting, not feature-gated).
		builder.Services.AddHostedService(sp => new PetBox.Core.Data.BackupService(
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
			builder.Services.AddHostedService(sp => sp.GetRequiredService<ChannelIngestionPipeline>());
			builder.Services.AddHostedService<PetBox.Log.Core.Retention.RetentionService>();
			builder.Services.AddHostedService<PetBox.Log.Core.Retention.LogOrphanCleanupService>();
		}
		if (new FeatureFlags(builder.Configuration).IsEnabled(Feature.Dashboard))
		{
			builder.Services.AddHttpClient();
			builder.Services.AddHostedService<PetBox.Dashboard.HealthPoller>();
		}
		if (new FeatureFlags(builder.Configuration).IsEnabled(Feature.Data))
		{
			builder.Services.AddHostedService<PetBox.Data.OrphanCleanupService>();
			builder.Services.AddHostedService<PetBox.Data.WalCheckpointService>();
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
			Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(
				System.Text.Unicode.UnicodeRanges.BasicLatin,
				System.Text.Unicode.UnicodeRanges.Latin1Supplement,
				System.Text.Unicode.UnicodeRanges.LatinExtendedA,
				System.Text.Unicode.UnicodeRanges.GeneralPunctuation, // — … “” ‘’ etc.
				System.Text.Unicode.UnicodeRanges.Cyrillic),
		};
		builder.Services.AddMcpServer()
			.WithHttpTransport()
			.WithToolsFromAssembly(typeof(Program).Assembly, mcpJson)
			.WithRequestFilters(filters =>
			{
				PetBox.Web.Mcp.McpToolScopeFilter.Register(filters); // A7b: scope-trim tools/list
				PetBox.Web.Mcp.McpTracingFilter.Register(filters);   // span per tool call (self-tracing)
			});
		builder.Services.AddSingleton<FeatureFlags>();

		// Built-in .NET 10 OpenAPI document generation. The document is materialized at build
		// time into doc/api/PetBox.Web.json (see OpenApiGenerateDocuments in the csproj) and
		// served at runtime only in Development (MapOpenApi in Configure).
		builder.Services.AddOpenApi();

		builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));
		builder.Services.Configure<ConfigApiKeyOptions>(builder.Configuration.GetSection("Auth"));
		builder.Services.AddSingleton<ConfigApiKeyLookup>();
		builder.Services.AddScoped<DbApiKeyLookup>();
		builder.Services.AddScoped<IApiKeyLookup, CompositeApiKeyLookup>();

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
			builder.Services.AddHostedService<PetBox.Log.Core.SelfLogging.SystemLogFlusher>();
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
				o.AccessDeniedPath = "/Login";
				o.ExpireTimeSpan = TimeSpan.FromDays(7);
				o.SlidingExpiration = true;
			})
			.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
				ApiKeyAuthenticationHandler.SchemeName, null);

		builder.Services.AddAuthorizationBuilder()
			.AddPolicy("ApiKey", p => p
				.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
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
			});
		builder.Services.AddHttpContextAccessor();
		builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, ScopeAuthorizationHandler>();
		builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, WorkspaceRoleAuthorizationHandler>();
		builder.Services.AddScoped<INavigationContext, NavigationContext>();
		builder.Services.AddScoped<PetBox.Core.Settings.ISettingsResolver, PetBox.Web.Settings.SettingsResolver>();
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
		});
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

			// One-time, idempotent (spec-flat-tags): convert legacy path-keyed nodes to flat
			// slugs + synthesize part_of edges. Runs after the legacy fold so every board is in
			// its per-project file; needs PetBoxDb for the part_of edges (in petbox.db).
			using (var flatScope = app.Services.CreateScope())
			{
				var relations = new PetBox.Tasks.Data.RelationStore(flatScope.ServiceProvider.GetRequiredService<PetBoxDb>());
				var flatLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tasks.FlatNodeMigration");
				new PetBox.Tasks.Data.FlatNodePartOfMigrator(Path.Combine(dataDir, "tasks"), tasksFactory, relations, flatLog).Migrate();
			}


		using (var scope = app.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			var adminOptions = scope.ServiceProvider.GetRequiredService<IOptions<AdminOptions>>();
			AdminBootstrapper.EnsureAdminUser(db, adminOptions);

			// The petbox self-log is the one log created automatically — petbox's
			// own ILogger + Seq self-log write here. User logs are created explicitly.
			if (new FeatureFlags(configuration).IsEnabled(Feature.Logging))
			{
				var logStore = scope.ServiceProvider.GetRequiredService<ILogStore>();
				if (!logStore.ExistsAsync(LogNames.SystemProject, LogNames.SelfLog).GetAwaiter().GetResult())
					logStore.CreateAsync(LogNames.SystemProject, LogNames.SelfLog, "PetBox self-log")
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

		app.UseStaticFiles();
		app.UseRouting();
		app.UseAuthentication();
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

		// Serve the OpenAPI document (/openapi/v1.json) only in Development. Production exposes
		// no spec endpoint; the committed doc/api/PetBox.Web.json is the published contract.
		if (app.Environment.IsDevelopment())
			app.MapOpenApi();

		app.MapMethods("/health", ["GET", "HEAD"], () => TypedResults.Ok(new HealthStatusResponse("healthy")))
			.Produces<HealthStatusResponse>()
			.AllowAnonymous();

		app.MapMethods("/version", ["GET", "HEAD"], () => TypedResults.Ok(new VersionResponse(
			Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev",
			Environment.GetEnvironmentVariable("GIT_SHORT_SHA") ?? "local",
			Environment.GetEnvironmentVariable("GIT_COMMIT_DATE") ?? string.Empty)))
			.Produces<VersionResponse>()
			.AllowAnonymous();

		app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
		{
			await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
			return Results.Redirect("/Login");
		});

		app.MapAuthEndpoints();
		app.MapWorkspaceSwitch();
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
