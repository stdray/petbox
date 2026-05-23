using YobaBox.Core.Auth;
using YobaBox.Core.Data;
using YobaBox.Core.Features;

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
builder.Services.AddSingleton<FeatureFlags>();
builder.Services.AddRazorPages();

builder
	.AddConfigModule()
	.AddLogModule()
	.AddDataModule()
	.AddDashboardModule();

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

app
	.UseConfigModule()
	.UseLogModule()
	.UseDataModule()
	.UseDashboardModule();

app.Run();

public partial class Program;
