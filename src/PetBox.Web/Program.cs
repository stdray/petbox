using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Seq.Extensions.Logging;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Data;
using PetBox.Log.Core;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Ingestion;
using PetBox.Web;
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
	public static void ConfigureServices(WebApplicationBuilder builder)
	{
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
		builder.Services.AddSingleton<IScopedDbFactory<ConfigDb>>(sp => new ScopedDbFactory<ConfigDb>(
				Path.Combine(ResolveDataDir(sp), "config"), PetBox.Core.Settings.Scope.Workspace,
				cs => new ConfigDb(ConfigDb.CreateOptions(cs)), ConfigSchema.Ensure));
		builder.Services.AddSingleton<IConfigDbFactory>(sp => new ConfigDbFactory(sp.GetRequiredService<IScopedDbFactory<ConfigDb>>()));
		builder.Services.AddSingleton<PetBox.Data.IDataDbFactory>(sp => new PetBox.Data.DataDbFactory(Path.Combine(ResolveDataDir(sp), "db")));
		builder.Services.AddSingleton<PetBox.Data.Schema.SchemaRunner>();
		// Tasks / Memory / Sessions — scope-keyed temporal stores. Registered
		// unconditionally (like the log factory); feature flags gate nav, pages and
		// MCP tools, not DI. Each named board/store is its own file; sessions are a
		// single per-project file.
		builder.Services.AddSingleton<IScopedDbFactory<PetBox.Tasks.Data.TasksDb>>(sp => new ScopedDbFactory<PetBox.Tasks.Data.TasksDb>(
				Path.Combine(ResolveDataDir(sp), "tasks"), PetBox.Core.Settings.Scope.Project,
				cs => new PetBox.Tasks.Data.TasksDb(PetBox.Tasks.Data.TasksDb.CreateOptions(cs)), PetBox.Tasks.Data.TasksSchema.Ensure));
		builder.Services.AddScoped<PetBox.Tasks.Data.ITaskBoardStore, PetBox.Tasks.Data.TaskBoardStore>();
		builder.Services.AddSingleton<IScopedDbFactory<PetBox.Memory.Data.MemoryDb>>(sp => new ScopedDbFactory<PetBox.Memory.Data.MemoryDb>(
				Path.Combine(ResolveDataDir(sp), "memory"), PetBox.Core.Settings.Scope.Project,
				cs => new PetBox.Memory.Data.MemoryDb(PetBox.Memory.Data.MemoryDb.CreateOptions(cs)), PetBox.Memory.Data.MemorySchema.Ensure));
		builder.Services.AddScoped<PetBox.Memory.Data.IMemoryStore, PetBox.Memory.Data.MemoryStore>();
		builder.Services.AddSingleton<IScopedDbFactory<PetBox.Sessions.Data.SessionsDb>>(sp => new ScopedDbFactory<PetBox.Sessions.Data.SessionsDb>(
				Path.Combine(ResolveDataDir(sp), "sessions"), PetBox.Core.Settings.Scope.Project,
				cs => new PetBox.Sessions.Data.SessionsDb(PetBox.Sessions.Data.SessionsDb.CreateOptions(cs)), PetBox.Sessions.Data.SessionsSchema.Ensure));
		builder.Services.AddScoped<PetBox.Sessions.Data.ISessionStore, PetBox.Sessions.Data.SessionStore>();
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
		builder.Services.AddMcpServer()
			.WithHttpTransport()
			.WithToolsFromAssembly(typeof(Program).Assembly);
		builder.Services.AddSingleton<FeatureFlags>();
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
					.AddOtlpExporter(o =>
					{
						o.Endpoint = new Uri(otlpEndpoint);
						o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
						if (!string.IsNullOrWhiteSpace(otelApiKey))
							o.Headers = $"X-Seq-ApiKey={otelApiKey}";
					}));
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

		app.MapMethods("/health", ["GET", "HEAD"], () => Results.Ok(new { status = "healthy" }))
			.AllowAnonymous();

		app.MapMethods("/version", ["GET", "HEAD"], () => Results.Ok(new
		{
			semVer = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev",
			shortSha = Environment.GetEnvironmentVariable("GIT_SHORT_SHA") ?? "local",
			commitDate = Environment.GetEnvironmentVariable("GIT_COMMIT_DATE") ?? string.Empty,
		})).AllowAnonymous();

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
