using YobaBox.Config;
using YobaBox.Core.Auth;
using YobaBox.Core.Data;
using YobaBox.Core.Features;
using YobaBox.Log.Core;
using YobaBox.Log.Core.Data;
using YobaBox.Log.Core.Ingestion;
using YobaBox.Web;

if (args.Length >= 2 && args[0] == "--hash-password")
{
	Console.WriteLine(AdminPasswordHasher.Hash(args[1]));
	return;
}

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("YobaBox")
	?? "Data Source=./data/yobabox.db;Cache=Shared";
Directory.CreateDirectory(Path.GetDirectoryName(
	new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource)!);

builder.Services.AddScoped(_ => new YobaBoxDb(YobaBoxDb.CreateOptions(connectionString)));
builder.Services.AddScoped(_ => new LogDb(LogDb.CreateOptions(connectionString)));
builder.Services.AddSingleton<CleFParser>();
builder.Services.AddSingleton<FeatureFlags>();
builder.Services.AddRazorPages();

if (new FeatureFlags(builder.Configuration).IsEnabled("Config"))
{
	// Phase 1: ConfigApi registered
}

var app = builder.Build();

MigrationRunner.Run(connectionString);

if (!app.Environment.IsDevelopment())
{
	app.UseExceptionHandler("/Error");
	app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();

app.MapMethods("/health", ["GET", "HEAD"], () => Results.Ok(new { status = "healthy" }))
	.AllowAnonymous();

app.MapMethods("/version", ["GET", "HEAD"], () => Results.Ok(new
{
	semVer = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev",
	shortSha = Environment.GetEnvironmentVariable("GIT_SHORT_SHA") ?? "local",
	commitDate = Environment.GetEnvironmentVariable("GIT_COMMIT_DATE") ?? string.Empty,
})).AllowAnonymous();

app.MapAuthEndpoints();
app.MapRazorPages();

if (new FeatureFlags(app.Configuration).IsEnabled("Config"))
{
	app.MapConfigEndpoints();
}

if (new FeatureFlags(app.Configuration).IsEnabled("Logging"))
{
	app.MapLogEndpoints();
}

app.Run();

public partial class Program;
