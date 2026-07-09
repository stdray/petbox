using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Tasks;

// Shared per-class host for the Tasks methodology test classes — the same proven pattern
// as TasksMethodologySmokeFixture: ONE WebApplicationFactory + ONE MCP handshake for the
// whole class (xUnit news the test CLASS per test, so without this every test boots its
// own host — the single biggest wall-clock cost in the suite). Per-test DATA isolation is
// restored by ResetAsync: the core catalog rows (task_boards, relations) for the test
// project are wiped and the per-project tasks file (nodes, comments, tags, methodology
// definitions, version cursors, search index) is deleted outright, so every test still
// starts from an empty project.
public abstract class TasksMcpFixture : IAsyncLifetime
{
	readonly string _projectName;
	readonly string _baseDir;
	HttpClient _http = null!;

	public string ProjectKey { get; }
	public string AgentKey { get; }
	public WebApplicationFactory<Program> Factory { get; }
	public McpClient Mcp { get; private set; } = null!;

	protected TasksMcpFixture(string projectKey, string projectName)
	{
		ProjectKey = projectKey;
		AgentKey = $"yb_key_{projectKey}_agent"; // tasks:read,tasks:write
		_projectName = projectName;
		_baseDir = Path.Combine(Path.GetTempPath(), $"petbox-{projectKey}-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Environment.SetEnvironmentVariable("Features__Tasks", "true");

		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Features:Tasks"] = "true",
					});
				});
				b.ConfigureServices(svc =>
				{
					// Methodology tests only need MCP stack — background services
					// (vectorization, digest, orphan cleanup etc.) just create pooled
					// SqliteConnections that hold native file handles on Windows and
					// prevent ResetAsync from deleting per-test files.
					var hosted = svc.Where(d => typeof(IHostedService).IsAssignableFrom(d.ServiceType)).ToList();
					foreach (var h in hosted) svc.Remove(h);

					var tasksFactory = svc.SingleOrDefault(d => d.ServiceType == typeof(IScopedDbFactory<TasksDb>));
					if (tasksFactory is not null) svc.Remove(tasksFactory);
				svc.AddSingleton<IScopedDbFactory<TasksDb>>(_ => new ScopedDbFactory<TasksDb>(
					Path.Combine(_baseDir, "tasks"), PetBox.Core.Settings.Scope.Project,
					cs => new TasksDb(TasksDb.CreateOptions(cs)), TasksSchema.Ensure));
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using (var scope = Factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.ApiKeys.Where(k => k.Key == AgentKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == ProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();
			await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "test", Name = _projectName });
			await db.InsertAsync(new ApiKey { Key = AgentKey, ProjectKey = ProjectKey, Scopes = "tasks:read,tasks:write", CreatedAt = DateTime.UtcNow });
		}

		_http = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		_http.DefaultRequestHeaders.Add("X-Api-Key", AgentKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = AgentKey },
		}, _http);
		Mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
	}

	// Wipe everything the previous test may have written under the shared host, so each
	// test sees an empty project: catalog + edges live in petbox.db (task_boards,
	// relations); nodes/comments/tags/methodology definitions/version cursors/search index
	// all live in the per-project tasks file, which we delete wholesale.
	public async Task ResetAsync()
	{
		using (var scope = Factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.TaskBoards.Where(b => b.ProjectKey == ProjectKey).DeleteAsync();
			await db.Relations.Where(r => r.ProjectKey == ProjectKey).DeleteAsync();
		}

		var tasksFactory = Factory.Services.GetRequiredService<IScopedDbFactory<TasksDb>>();
		await tasksFactory.EvictAsync(ProjectKey);
		var path = Path.Combine(_baseDir, "tasks", ProjectKey + ".db");
		if (File.Exists(path))
		{
			using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
			conn.Open();
			using var cmd = conn.CreateCommand();
			cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
			cmd.ExecuteNonQuery();
		}
		SqliteConnection.ClearAllPools();
		for (var attempt = 0; attempt < 5; attempt++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			if (ScopedDbFiles.TryDelete(path))
				return;
			Task.Delay(100).GetAwaiter().GetResult();
		}
		throw new InvalidOperationException($"per-test reset could not delete {path} (still locked)");
	}

	public async Task DisposeAsync()
	{
		await Mcp.DisposeAsync();
		_http.Dispose();
		await Factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

public sealed class MethodologyDefinitionFixture : TasksMcpFixture
{
	public MethodologyDefinitionFixture() : base("mdef", "Methodology def") { }
}

public sealed class MethodologyGuideFixture : TasksMcpFixture
{
	public MethodologyGuideFixture() : base("mgd", "Guide") { }
}

public sealed class MethodologyMigrationFixture : TasksMcpFixture
{
	public MethodologyMigrationFixture() : base("mmig", "Migration") { }
}

public sealed class MethodologyPrimitivesFixture : TasksMcpFixture
{
	public MethodologyPrimitivesFixture() : base("mprm", "Primitives") { }
}

public sealed class MethodologyRuntimeFixture : TasksMcpFixture
{
	public MethodologyRuntimeFixture() : base("mrt", "Runtime") { }
}
