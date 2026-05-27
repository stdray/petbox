using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Seq.Extensions.Logging;
using YobaBox.Config;
using YobaBox.Config.Data;
using YobaBox.Core.Auth;
using YobaBox.Core.Data;
using YobaBox.Core.Features;
using YobaBox.Data;
using YobaBox.Log.Core;
using YobaBox.Log.Core.Data;
using YobaBox.Log.Core.Ingestion;
using YobaBox.Web;
using YobaBox.Web.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using YobaBox.Core.Models;

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
		var connectionString = builder.Configuration.GetConnectionString("YobaBox")
			?? "Data Source=./data/yobabox.db;Cache=Shared";
		Directory.CreateDirectory(Path.GetDirectoryName(
			new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource)!);

		builder.Services.AddScoped(_ => new YobaBoxDb(YobaBoxDb.CreateOptions(connectionString)));
		builder.Services.AddSingleton<ILogDbFactory>(_ => new LogDbFactory(
			Path.Combine(Path.GetDirectoryName(
				new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource)!, "logs")));
		builder.Services.AddSingleton<IConfigDbFactory>(_ => new ConfigDbFactory(
			Path.Combine(Path.GetDirectoryName(
				new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource)!, "config")));
		var masterKey = builder.Configuration["YobaBox:MasterKey"]
			?? Environment.GetEnvironmentVariable("YOBABOX_MASTER_KEY");
		builder.Services.AddSingleton(Options.Create(new SecretEncryptorOptions { MasterKey = masterKey }));
		builder.Services.AddSingleton<ISecretEncryptor, AesGcmSecretEncryptor>();
		builder.Services.AddMemoryCache();
		builder.Services.AddSingleton<CleFParser>();
		builder.Services.AddSingleton<ITailBroadcaster, InMemoryTailBroadcaster>();
		builder.Services.Configure<IngestionOptions>(
			builder.Configuration.GetSection("Ingestion"));
		builder.Services.Configure<YobaBox.Log.Core.Retention.RetentionOptions>(
			builder.Configuration.GetSection("Retention"));
		if (new FeatureFlags(builder.Configuration).IsEnabled("Logging"))
		{
			builder.Services.AddSingleton<ChannelIngestionPipeline>();
			builder.Services.AddSingleton<IIngestionPipeline>(sp => sp.GetRequiredService<ChannelIngestionPipeline>());
			builder.Services.AddHostedService(sp => sp.GetRequiredService<ChannelIngestionPipeline>());
			builder.Services.AddHostedService<YobaBox.Log.Core.Retention.RetentionService>();
		}
		builder.Services.AddSingleton<FeatureFlags>();
		builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));

		builder.Logging.Configure(o => o.ActivityTrackingOptions =
			ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

		var selfLogEnabled = builder.Configuration.GetValue("Seq:SelfLog:Enabled", false);
		if (selfLogEnabled && new FeatureFlags(builder.Configuration).IsEnabled("Logging"))
		{
			var staticProps = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, System.Text.Json.JsonElement>();
			staticProps["Env"] = System.Text.Json.JsonSerializer.SerializeToElement(builder.Environment.EnvironmentName);
			staticProps["Ver"] = System.Text.Json.JsonSerializer.SerializeToElement(Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev");
			staticProps["Sha"] = System.Text.Json.JsonSerializer.SerializeToElement(Environment.GetEnvironmentVariable("GIT_SHORT_SHA") ?? "local");
			staticProps["Host"] = System.Text.Json.JsonSerializer.SerializeToElement(Environment.MachineName);

			builder.Services.Configure<YobaBox.Log.Core.SelfLogging.SystemLoggerOptions>(
				builder.Configuration.GetSection("SelfLogging"));
			builder.Services.PostConfigure<YobaBox.Log.Core.SelfLogging.SystemLoggerOptions>(o =>
				o.StaticProperties = staticProps.ToImmutable());

			builder.Services.AddSingleton<YobaBox.Log.Core.SelfLogging.SystemLoggerProvider>();
			builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(
				sp => sp.GetRequiredService<YobaBox.Log.Core.SelfLogging.SystemLoggerProvider>());
			builder.Services.AddHostedService<YobaBox.Log.Core.SelfLogging.SystemLogFlusher>();
		}

		var otelEnabled = builder.Configuration.GetValue("OpenTelemetry:Enabled", false);
		var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
		if (otelEnabled && !string.IsNullOrWhiteSpace(otlpEndpoint))
		{
			var serviceName = builder.Configuration["OpenTelemetry:ServiceName"] ?? "yobabox";
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
							&& !ctx.Request.Path.StartsWithSegments("/api/events/raw");
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
					ctx.Request.Headers.ContainsKey("X-Api-Key")
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
				p.AddRequirements(new ScopeRequirement("config:read"));
			})
			.AddPolicy("ConfigWrite", p =>
			{
				p.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName);
				p.AddRequirements(new ScopeRequirement("config:write"));
			})
			.AddPolicy("DataRead", p =>
			{
				p.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName);
				p.AddRequirements(new ScopeRequirement("data:read"));
			})
			.AddPolicy("DataWrite", p =>
			{
				p.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName);
				p.AddRequirements(new ScopeRequirement("data:write"));
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
		builder.Services.AddRazorPages(options =>
		{
			// Cross-project logs/traces — same page, workspace-only route (no projectKey).
			options.Conventions.AddPageRoute("/Logs/Index", "/ui/{workspaceKey}/logs");
			options.Conventions.AddPageRoute("/Logs/Traces", "/ui/{workspaceKey}/traces");
			// Project-scoped Config — same Config/Index page, applies project:{projectKey} filter.
			options.Conventions.AddPageRoute("/Config/Index", "/ui/{workspaceKey}/{projectKey}/config");
			options.Conventions.AddPageRoute("/Config/Editor", "/ui/{workspaceKey}/{projectKey}/config/editor/{bindingId:long?}");
			options.Conventions.AddPageRoute("/Config/History", "/ui/{workspaceKey}/{projectKey}/config/history");
			options.Conventions.AddPageRoute("/Config/Preview", "/ui/{workspaceKey}/{projectKey}/config/preview");
		});
	}

	public static void Configure(WebApplication app)
	{
		var connectionString = app.Configuration.GetConnectionString("YobaBox")
			?? "Data Source=./data/yobabox.db;Cache=Shared";

		MigrationRunner.Run(connectionString);

		using (var scope = app.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<YobaBoxDb>();
			var adminOptions = scope.ServiceProvider.GetRequiredService<IOptions<AdminOptions>>();
			AdminBootstrapper.EnsureAdminUser(db, adminOptions);
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

		if (new FeatureFlags(app.Configuration).IsEnabled("Config"))
		{
			app.MapConfigEndpoints();
		}

		if (new FeatureFlags(app.Configuration).IsEnabled("Logging"))
		{
			app.MapLogEndpoints();
			app.MapShareEndpoints();

			if (app.Configuration.GetValue("Seq:SelfLog:Enabled", false))
				app.MapSeqSelfLogEndpoint();
		}

		if (new FeatureFlags(app.Configuration).IsEnabled("Data"))
		{
			app.MapDataEndpoints();
		}
	}
}
