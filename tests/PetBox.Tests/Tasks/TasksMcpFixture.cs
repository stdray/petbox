using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Data;
using PetBox.Tests.Support;

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
	string AgentKey { get; }
	public WebApplicationFactory<Program> Factory { get; }
	public McpClient Mcp { get; private set; } = null!;

	readonly string _extraScopes;

	// extraFeatures/extraScopes: additive-only, defaulted to nothing so the seven existing
	// subclasses are byte-for-byte unaffected. A subclass that needs another module's tools
	// reachable over the real MCP wire (e.g. memory_search/config_binding_search for the
	// curated-hint tests, work unknown-param-curated-hints) turns it on here instead of standing
	// up a second, hand-rolled WebApplicationFactory — ResolveDataDir (Program.cs) derives every
	// module's data directory from ConnectionStrings:PetBox, which is already unique per fixture
	// instance, so switching Memory/Config on cannot collide with another fixture's files.
	protected TasksMcpFixture(string projectKey, string projectName,
		IReadOnlyList<string>? extraFeatures = null, string extraScopes = "")
	{
		ProjectKey = projectKey;
		AgentKey = $"yb_key_{projectKey}_agent"; // tasks:read,tasks:write(+extraScopes)
		_projectName = projectName;
		_extraScopes = extraScopes;
		_baseDir = Path.Combine(Path.GetTempPath(), $"petbox-{projectKey}-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");

		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				// Features:Tasks gates a registration at BUILD time (Program.cs, before
				// builder.Build()). UseSetting IS visible at that pre-Build read (measured:
				// Architecture/ConfigVisibilityContractTests) — a process-global env var is
				// unnecessary and was leaking into every other test in the process
				// (chore/tests-env-leak).
				b.UseSetting("Features:Tasks", "true");
				foreach (var feature in extraFeatures ?? [])
					b.UseSetting($"Features:{feature}", "true");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					var settings = new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Features:Tasks"] = "true",
						// Methodology tests only need the MCP stack — background services
						// (vectorization, digest, orphan cleanup etc.) just create pooled
						// SqliteConnections that hold native file handles on Windows and
						// prevent ResetAsync from deleting per-test files. The host's OWN
						// off-switch, not surgery on its DI container (spec:
						// host-composition-contract).
						["Host:BackgroundServices"] = "false",
					};
					foreach (var feature in extraFeatures ?? []) settings[$"Features:{feature}"] = "true";
					cfg.AddInMemoryCollection(settings);
				});
				b.ConfigureServices(svc =>
				{
					var tasksFactory = svc.SingleOrDefault(d => d.ServiceType == typeof(IScopedDbFactory<TasksDb>));
					if (tasksFactory is not null) svc.Remove(tasksFactory);
					svc.AddSingleton<IScopedDbFactory<TasksDb>>(_ => new ScopedDbFactory<TasksDb>(
						Path.Combine(_baseDir, "tasks"), PetBox.Core.Settings.Scope.Project,
						cs => new TasksDb(TasksDb.CreateOptions(cs)), TestSchema.Tasks));
				});
			});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using (var scope = Factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			await db.ApiKeys.Where(k => k.Key == AgentKey).DeleteAsync();
			await db.Projects.Where(p => p.Key == ProjectKey).DeleteAsync();
			await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();
			await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "test", Name = _projectName });
			// methodology:write: these suites PROVISION a methodology (and edit live rules) as
			// fixture setup, which the spec methodology-write-scope gates separately from
			// tasks:write. The authz boundary itself is asserted in McpModuleToolsTests, not here.
			var scopes = "tasks:read,tasks:write,methodology:write"
				+ (_extraScopes.Length > 0 ? "," + _extraScopes : "");
			await db.InsertAsync(new ApiKey { Key = AgentKey, ProjectKey = ProjectKey, Scopes = scopes, CreatedAt = DateTime.UtcNow });
		}

		_http = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		_http.DefaultRequestHeaders.Add("X-Api-Key", AgentKey);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(_http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = AgentKey },
		}, _http);
		Mcp = await McpTestClient.ConnectAsync(transport);
	}

	// Wipe everything the previous test may have written under the shared host, so each
	// test sees an empty project: only the board CATALOG lives in petbox.db (task_boards);
	// nodes/edges/comments/tags/methodology definitions/version cursors/search index all
	// live in the per-project tasks file, whose rows we wipe (relations moved there
	// — relations-in-project-db — so they go with them).
	//
	// Rows, not the file. This used to evict the factory entry and call TestDirs.ResetDbFile,
	// which clears the file's pools, WAL-checkpoints it, then forces a full blocking GC to run
	// SqliteConnection finalizers so Windows will let the file be deleted. Instrumented over a
	// full run, that path cost 176-559 ms per call — more than creating the database from
	// scratch (a templated copy is 1-14 ms), and GC.Collect stops every thread in a 16-way
	// parallel suite, so the measured figure understates it. Deleting rows from an already
	// migrated file leaves the same empty-project state without touching pools, the WAL, the
	// collector, or the filesystem.
	public async Task ResetAsync()
	{
		using (var scope = Factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			await db.TaskBoards.Where(b => b.ProjectKey == ProjectKey).DeleteAsync();
		}

		var tasksFactory = Factory.Services.GetRequiredService<IScopedDbFactory<TasksDb>>();
		using var tasks = tasksFactory.NewEnsuredConnection(ProjectKey);
		TestDataReset.WipeAllTables(tasks);
	}

	// v3's IAsyncLifetime extends IAsyncDisposable, so CA1816 now applies to this unsealed type.
	public async ValueTask DisposeAsync()
	{
		GC.SuppressFinalize(this);
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

public sealed class MethodologySetDescriptionFixture : TasksMcpFixture
{
	public MethodologySetDescriptionFixture() : base("mdsc", "SetDescription") { }
}

public sealed class ObservationPromoteBodyFixture : TasksMcpFixture
{
	public ObservationPromoteBodyFixture() : base("opbe", "ObservationPromoteBody") { }
}
