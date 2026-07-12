using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// W3 (spec project-ref-must-exist) — McpProjectExistsFilter. A call that NAMES a project which is not
// in the registry is rejected, and — the part that matters — the per-project store is NOT created as a
// side effect of the attempt. Before W3 a "*" key with a typo'd projectKey silently materialized a new
// tasks/memory DB and wrote there; the write "succeeded", in a project nobody has.
//
// The registry (core.db `Projects`) is the authority, and the check runs on the RESOLVED projectKey —
// the caller's explicit one OR the default W2's leg 1 injected — because the filter is registered
// INNERMOST, after McpProjectDefaultFilter.
public sealed class ProjectExistsFilterFixture : IAsyncLifetime
{
	public const string Workspace = "w3ws";
	public const string RealProject = "kpvotes";     // the near-miss target
	public const string TypoProject = "kpvots";      // …and the typo an agent actually makes
	public const string GoneProject = "w3gone";      // created, then DELETED — a stale key default

	public const string StarRealKey = "yb_key_w3_star_real";
	public const string StarGoneKey = "yb_key_w3_star_gone";
	public const string StarBareKey = "yb_key_w3_star_bare";

	const string Scopes = "admin:provision,tasks:read,tasks:write,memory:read,memory:write";

	readonly WebApplicationFactory<Program> _factory;
	readonly List<HttpClient> _clients = [];
	readonly List<McpClient> _mcps = [];

	public string DataDir { get; private set; } = "";
	public McpClient StarReal { get; private set; } = null!;   // "*" key, default = RealProject
	public McpClient StarGone { get; private set; } = null!;   // "*" key, default = a DELETED project
	public McpClient StarBare { get; private set; } = null!;   // "*" key, NO default — nothing resolves

	public ProjectExistsFilterFixture()
	{
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
				["Features:Tasks"] = "true",
				["Features:Memory"] = "true",
			}));
		});
	}

	public async Task InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		DataDir = Path.GetDirectoryName(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(cs).DataSource)!;

		using (var scope = _factory.Services.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			await db.InsertAsync(new Workspace { Key = Workspace, Name = "W3", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = RealProject, WorkspaceKey = Workspace, Name = "Real" });

			// A project that EXISTED when the key was minted and is gone now: the stale-default path.
			await db.InsertAsync(new Project { Key = GoneProject, WorkspaceKey = Workspace, Name = "Gone" });
			await db.Projects.Where(p => p.Key == GoneProject).DeleteAsync();

			await db.InsertAsync(new ApiKey
			{
				Key = StarRealKey,
				ProjectKey = ProjectScope.AllProjects,
				Scopes = Scopes,
				DefaultProjectKey = RealProject,
				CreatedAt = DateTime.UtcNow,
			});
			await db.InsertAsync(new ApiKey
			{
				Key = StarGoneKey,
				ProjectKey = ProjectScope.AllProjects,
				Scopes = Scopes,
				DefaultProjectKey = GoneProject,
				CreatedAt = DateTime.UtcNow,
			});

			// The key that resolves NOTHING: "*" claim, no default. A blank projectKey from this key
			// has nowhere to go — and used to end up in a literal `tasks/.db` all the same.
			await db.InsertAsync(new ApiKey
			{
				Key = StarBareKey,
				ProjectKey = ProjectScope.AllProjects,
				Scopes = Scopes,
				CreatedAt = DateTime.UtcNow,
			});
		}

		StarReal = await ConnectAsync(StarRealKey);
		StarGone = await ConnectAsync(StarGoneKey);
		StarBare = await ConnectAsync(StarBareKey);
	}

	public IServiceScope Scope() => _factory.Services.CreateScope();

	// The per-project tasks DB — the store whose LAZY creation is the bug: it appears on first write.
	public string TasksDbOf(string project) => Path.Combine(DataDir, "tasks", $"{project}.db");

	// …and the memory one, for the paths that resolve the project INSIDE the tool (memory_remember).
	public string MemoryDbOf(string project) => Path.Combine(DataDir, "memory", $"{project}.db");

	async Task<McpClient> ConnectAsync(string apiKey)
	{
		var http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		_clients.Add(http);
		var transport = new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(http.BaseAddress!, "/mcp"),
			AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = apiKey },
		}, http);
		var mcp = await McpClient.CreateAsync(transport, cancellationToken: default);
		_mcps.Add(mcp);
		return mcp;
	}

	public async Task DisposeAsync()
	{
		foreach (var mcp in _mcps) await mcp.DisposeAsync();
		foreach (var http in _clients) http.Dispose();
		await _factory.DisposeAsync();
	}
}

public sealed class ProjectExistsFilterTests : IClassFixture<ProjectExistsFilterFixture>
{
	readonly ProjectExistsFilterFixture _fx;
	public ProjectExistsFilterTests(ProjectExistsFilterFixture fx) => _fx = fx;

	// tasks_board_create is the probe: it WRITES, so a call that gets through materializes
	// {dataDir}/tasks/{project}.db + a core.db TaskBoards row — both observable.
	static async Task<CallToolResult> BoardCreateAsync(McpClient mcp, string? projectKey, string board)
	{
		var args = new Dictionary<string, object?> { ["board"] = board };
		if (projectKey is not null) args["projectKey"] = projectKey;
		return await (await Tool(mcp, "tasks_board_create")).CallAsync(args);
	}

	static async Task<McpClientTool> Tool(McpClient mcp, string name) =>
		(await mcp.ListToolsAsync()).First(t => t.Name == name);

	static string Text(CallToolResult result) =>
		string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

	async Task<bool> HasBoardRowAsync(string project)
	{
		using var scope = _fx.Scope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		return await db.TaskBoards.AnyAsync(b => b.ProjectKey == project);
	}

	// THE bug: a "*" key names a project that does not exist. Rejected — and nothing is created.
	[Fact]
	public async Task UnknownProject_IsRejected_AndNoStoreIsCreated()
	{
		var result = await BoardCreateAsync(_fx.StarReal, ProjectExistsFilterFixture.TypoProject, "w3board");

		result.IsError.Should().Be(true);
		Text(result).Should().Contain(ProjectExistsFilterFixture.TypoProject).And.Contain("does not exist");

		File.Exists(_fx.TasksDbOf(ProjectExistsFilterFixture.TypoProject)).Should().BeFalse(
			"a rejected project reference must not materialize per-project storage");
		(await HasBoardRowAsync(ProjectExistsFilterFixture.TypoProject)).Should().BeFalse(
			"…nor a catalog row for it");
	}

	// The typo is the dominant cause, so the rejection has to name the near miss.
	[Fact]
	public async Task Rejection_NamesTheNearMissProject()
	{
		var result = await BoardCreateAsync(_fx.StarReal, ProjectExistsFilterFixture.TypoProject, "w3board2");

		// (the envelope is JSON — the quotes around the key are '-escaped in it, so match the key)
		Text(result).Should().Contain("Did you mean").And.Contain(ProjectExistsFilterFixture.RealProject,
			"'kpvots' is one deletion away from 'kpvotes' — say so");
	}

	// The control: the same call against a project that DOES exist works, and DOES create the store —
	// so the assertion above is about the reject, not about a probe that never writes anything.
	[Fact]
	public async Task ExistingProject_Works_AndCreatesItsStore()
	{
		var result = await BoardCreateAsync(_fx.StarReal, ProjectExistsFilterFixture.RealProject, "w3real");

		result.IsError.Should().NotBe(true, Text(result));
		File.Exists(_fx.TasksDbOf(ProjectExistsFilterFixture.RealProject)).Should().BeTrue();
	}

	// W2's INJECTED default is checked too: a key whose default names a since-deleted project is
	// rejected on a call that never mentioned a project at all.
	[Fact]
	public async Task InjectedDefault_NamingADeletedProject_IsRejected()
	{
		var result = await BoardCreateAsync(_fx.StarGone, null, "w3ghost");

		result.IsError.Should().Be(true);
		Text(result).Should().Contain(ProjectExistsFilterFixture.GoneProject).And.Contain("does not exist");
		File.Exists(_fx.TasksDbOf(ProjectExistsFilterFixture.GoneProject)).Should().BeFalse();
	}

	// project_create names a project that does not exist YET — by definition. It is outside the filter
	// (its parameter is `key`, not `projectKey`), and the project it creates is immediately usable:
	// lazy store creation on first write is intact, it is only the UNREGISTERED project that is refused.
	[Fact]
	public async Task ProjectCreate_ThenFirstWrite_StillWorks()
	{
		var created = await (await Tool(_fx.StarReal, "project_create")).CallAsync(new Dictionary<string, object?>
		{
			["workspaceKey"] = ProjectExistsFilterFixture.Workspace,
			["key"] = "w3fresh",
		});
		created.IsError.Should().NotBe(true, Text(created));

		var board = await BoardCreateAsync(_fx.StarReal, "w3fresh", "w3new");

		board.IsError.Should().NotBe(true, Text(board));
		File.Exists(_fx.TasksDbOf("w3fresh")).Should().BeTrue("the store is still created lazily on first write");
	}

	// The wildcard "*" in a projectKey slot is a claim sentinel on the apikey_* tools, which address
	// keys BY claim: apikey_list('*') lists the cross-project keys. There it must NOT be "rejected as
	// an unknown project" — the control for the rejections below.
	[Fact]
	public async Task WildcardProjectKey_IsNotAProjectReference_OnTheApiKeyTools()
	{
		var result = await (await Tool(_fx.StarReal, "apikey_list")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = ProjectScope.AllProjects,
		});

		result.IsError.Should().NotBe(true, Text(result));
		result.StructuredContent!.Value.GetProperty("keys").EnumerateArray()
			.Select(k => k.GetProperty("key").GetString())
			.Should().Contain(ProjectExistsFilterFixture.StarRealKey);
	}

	// F3 — …and NOWHERE else. On a tool that routes STORAGE by projectKey, "*" is a file name:
	// Authorizes("*","*") is true and ScopedDbFiles.PathFor does not sanitize, so this used to create
	// a literal `tasks/*.db`. An agent copying the "*" out of whoami into a projectKey slot is real.
	[Fact]
	public async Task WildcardProjectKey_IsRefused_OnAStorageTool()
	{
		var result = await BoardCreateAsync(_fx.StarReal, ProjectScope.AllProjects, "w3star");

		result.IsError.Should().Be(true);
		Text(result).Should().Contain("is not a project");
		(await HasBoardRowAsync(ProjectScope.AllProjects)).Should().BeFalse();
		Directory.Exists(Path.Combine(_fx.DataDir, "tasks")).Should().BeTrue();
		Directory.EnumerateFiles(Path.Combine(_fx.DataDir, "tasks"))
			.Select(Path.GetFileName)
			.Should().NotContain(f => f!.Contains('*'), "'*' must never become a store file name");
	}

	// F3 — a BLANK projectKey means ABSENT (the resolver's job), everywhere. On a key that resolves
	// NOTHING there is nothing to fall back to: the call fails, and — the part that matters — it does
	// not create `tasks/.db` on the way out. (AssertProject("") used to be TRUE for a "*" claim.)
	[Fact]
	public async Task BlankProjectKey_Refused_WhenTheKeyResolvesNoDefault_AndCreatesNoStore()
	{
		var result = await BoardCreateAsync(_fx.StarBare, "", "w3blank");

		result.IsError.Should().Be(true, Text(result));
		File.Exists(Path.Combine(_fx.DataDir, "tasks", ".db")).Should().BeFalse(
			"a blank projectKey must never reach store creation");
		(await HasBoardRowAsync("")).Should().BeFalse();
	}

	// …and on a key that DOES resolve one, blank means exactly what an omitted projectKey means: the
	// key's default. Same rule, one meaning — no drift from ModuleMcp.ResolveProject.
	[Fact]
	public async Task BlankProjectKey_MeansAbsent_AndResolvesTheKeysDefault()
	{
		var result = await BoardCreateAsync(_fx.StarReal, "   ", "w3blankdefault");

		result.IsError.Should().NotBe(true, Text(result));
		(await HasBoardRowAsync(ProjectExistsFilterFixture.RealProject)).Should().BeTrue();
		File.Exists(Path.Combine(_fx.DataDir, "tasks", ".db")).Should().BeFalse();
	}

	// F2 — the projectKey the tool resolves INSIDE itself (memory_remember & co: projectKey is OPTIONAL,
	// so the default filter deliberately does not inject and the filter never saw it). A "*" key whose
	// default names a DELETED project would auto-vivify memory/<gone>.db + a MemoryStores row for a
	// project nobody has — the very write path the default-project feature enabled.
	[Fact]
	public async Task ResolvedDefault_NamingADeletedProject_IsRefused_OnTheOptionalProjectKeyPath()
	{
		var result = await (await Tool(_fx.StarGone, "memory_remember")).CallAsync(new Dictionary<string, object?>
		{
			["text"] = "a fact that must not land in a ghost project",
			["type"] = "Project",
		});

		result.IsError.Should().Be(true);
		Text(result).Should().Contain(ProjectExistsFilterFixture.GoneProject).And.Contain("does not exist");
		File.Exists(_fx.MemoryDbOf(ProjectExistsFilterFixture.GoneProject)).Should().BeFalse(
			"the resolved default is a project reference too — an unknown one creates no store");

		using var scope = _fx.Scope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		(await db.MemoryStores.AnyAsync(m => m.ProjectKey == ProjectExistsFilterFixture.GoneProject))
			.Should().BeFalse("…nor a catalog row");
	}

	// F4 — a $ws-<key> container names a WORKSPACE, and its Projects row is created LAZILY on first
	// resolve. Checking it against the PROJECT registry refused the first-ever direct write to a fresh
	// workspace's shared memory, with advice that cannot be followed (project_create forbids '$').
	[Fact]
	public async Task WorkspaceContainer_FirstDirectWrite_Succeeds()
	{
		var container = WorkspaceMemory.ContainerKeyFor(ProjectExistsFilterFixture.Workspace);

		using (var scope = _fx.Scope())
		{
			var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
			(await db.Projects.AnyAsync(p => p.Key == container)).Should().BeFalse(
				"the container's Projects row does not exist until something resolves it — that IS the case under test");
		}

		var result = await (await Tool(_fx.StarReal, "memory_store_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = container,
			["store"] = "shared",
		});

		result.IsError.Should().NotBe(true, Text(result));
		File.Exists(_fx.MemoryDbOf(container)).Should().BeTrue();
	}

	// …and a typo'd container still names no workspace, so it is still refused (and says so in the
	// terms of the thing it actually names).
	[Fact]
	public async Task WorkspaceContainer_NamingNoWorkspace_IsRefused()
	{
		var result = await (await Tool(_fx.StarReal, "memory_store_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = "$ws-nosuch",
			["store"] = "shared",
		});

		result.IsError.Should().Be(true);
		Text(result).Should().Contain("names no workspace");
		File.Exists(_fx.MemoryDbOf("$ws-nosuch")).Should().BeFalse();
	}

	// The legacy $system container is a workspace reference too — it names the seeded "$system" workspace.
	[Fact]
	public async Task SystemWorkspaceContainer_IsAcceptedAsAWorkspaceReference()
	{
		var result = await (await Tool(_fx.StarReal, "memory_store_create")).CallAsync(new Dictionary<string, object?>
		{
			["projectKey"] = WorkspaceMemory.SystemContainer,
			["store"] = "w3shared",
		});

		result.IsError.Should().NotBe(true, Text(result));
	}

	// THE COUPLING the review found untested: the exists filter must stay INSIDE McpErrorEnvelopeFilter.
	// Registered outside it, the throw would surface as the framework's opaque error and stop being the
	// structured { error: { type, message, detail } } body every other reject uses — agents parse `.error`.
	[Fact]
	public async Task Rejection_IsAStructuredErrorEnvelope()
	{
		var result = await BoardCreateAsync(_fx.StarReal, ProjectExistsFilterFixture.TypoProject, "w3envelope");

		result.IsError.Should().Be(true);
		result.StructuredContent.Should().BeNull("an error carries no structuredContent");

		var error = JsonDocument.Parse(Text(result)).RootElement.GetProperty("error");
		error.GetProperty("type").GetString().Should().Be(nameof(InvalidOperationException));
		error.GetProperty("message").GetString().Should().Contain("does not exist");
		error.TryGetProperty("detail", out _).Should().BeTrue();
	}

	// F2 (the other half) — deleting a project must not leave a DANGLING default on a surviving key.
	// A cross-project key is not deleted with the project (its claim is "*"), so its DefaultProjectKey
	// would keep naming a ghost — the exact state StarGone simulates. Null it out.
	[Fact]
	public async Task ProjectDeletion_NullsOutADanglingDefaultProjectKey()
	{
		using var scope = _fx.Scope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		const string project = "w3doomed";
		const string key = "yb_key_w3_dangling";

		await db.InsertAsync(new Project { Key = project, WorkspaceKey = ProjectExistsFilterFixture.Workspace, Name = "Doomed" });
		await db.InsertAsync(new ApiKey
		{
			Key = key,
			ProjectKey = ProjectScope.AllProjects,
			Scopes = "tasks:read",
			DefaultProjectKey = project,
			CreatedAt = DateTime.UtcNow,
		});

		(await ProjectDeletion.DeleteAsync(db, project)).Should().BeTrue();

		var survivor = await db.ApiKeys.FirstOrDefaultAsync(k => k.Key == key);
		survivor.Should().NotBeNull("a '*' key survives the deletion of the project it merely defaulted to");
		survivor!.DefaultProjectKey.Should().BeNull("…and its default must not keep naming a project that is gone");
	}

	// The suggester itself: near misses only. A key that resembles nothing in the registry gets an
	// honest "no similar project" instead of a random one.
	[Theory]
	[InlineData("kpvots", "kpvotes")]      // one deletion
	[InlineData("kpvotess", "kpvotes")]    // one insertion
	[InlineData("kpvotes-bot", "kpvotes")] // a prefix relative
	public void Suggest_FindsTheNearMiss(string typo, string expected) =>
		McpProjectExistsFilter.Suggest(typo, ["$system", "kpvotes", "petbox"]).Should().Contain(expected);

	[Fact]
	public void Suggest_IsSilent_WhenNothingIsClose() =>
		McpProjectExistsFilter.Suggest("zzzqqq", ["$system", "kpvotes", "petbox"]).Should().BeEmpty();

	// F5 — a 1-char reference is a fragment, not a typo: a prefix match used to score 0, so "k"
	// "suggested" every project starting with k as a perfect match.
	[Fact]
	public void Suggest_IgnoresAOneCharFragment() =>
		McpProjectExistsFilter.Suggest("k", ["kpvotes", "kpx", "petbox"]).Should().BeEmpty();
}
