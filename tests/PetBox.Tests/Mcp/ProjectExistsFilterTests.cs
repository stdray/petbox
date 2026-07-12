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

	const string Scopes = "admin:provision,tasks:read,tasks:write,memory:read,memory:write";

	readonly WebApplicationFactory<Program> _factory;
	readonly List<HttpClient> _clients = [];
	readonly List<McpClient> _mcps = [];

	public string DataDir { get; private set; } = "";
	public McpClient StarReal { get; private set; } = null!;   // "*" key, default = RealProject
	public McpClient StarGone { get; private set; } = null!;   // "*" key, default = a DELETED project

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
		}

		StarReal = await ConnectAsync(StarRealKey);
		StarGone = await ConnectAsync(StarGoneKey);
	}

	public IServiceScope Scope() => _factory.Services.CreateScope();

	// The per-project tasks DB — the store whose LAZY creation is the bug: it appears on first write.
	public string TasksDbOf(string project) => Path.Combine(DataDir, "tasks", $"{project}.db");

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

	// The wildcard "*" in a projectKey slot is a claim sentinel, not a project reference: apikey_list('*')
	// lists the cross-project keys. The filter must not "reject" it as an unknown project.
	[Fact]
	public async Task WildcardProjectKey_IsNotAProjectReference()
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
}
