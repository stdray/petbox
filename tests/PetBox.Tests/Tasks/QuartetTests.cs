using System.Security.Claims;
using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Tasks;

// methodology-quartet: opt-in provisioning of the four singleton boards + auto-wiring
// work->spec, the one-per-project singleton guard, and the unified surface.
[Collection("DataModule")]
public sealed class QuartetTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public QuartetTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-quartet-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		MigrationRunner.Run(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_tasks = new TasksService(new TaskBoardStore(_db, _factory), new RelationStore(_db), new TagStore(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	[Fact]
	public async Task Enable_ProvisionsQuartet_AutoWires_AndIsIdempotent()
	{
		var http = Http("tasks:read,tasks:write");
		var en = Json(await TasksTools.MethodologyEnableAsync(http, Flags(), _tasks, Proj));
		en.GetProperty("enabled").GetBoolean().Should().BeTrue();
		en.GetProperty("boards").EnumerateArray().Select(b => b.GetProperty("kind").GetString())
			.Should().Equal("intake", "ideas", "spec", "work"); // pipeline order

		// work board auto-wired to the spec board.
		var boards = Json(await TasksTools.BoardListAsync(http, Flags(), _tasks, Proj)).GetProperty("boards");
		var work = boards.EnumerateArray().Single(b => b.GetProperty("kind").GetString() == "work");
		work.GetProperty("specBoard").GetString().Should().Be("spec");

		// Idempotent: a rerun keeps exactly four methodology boards.
		await TasksTools.MethodologyEnableAsync(http, Flags(), _tasks, Proj);
		Json(await TasksTools.BoardListAsync(http, Flags(), _tasks, Proj)).GetProperty("boards")
			.EnumerateArray().Count().Should().Be(4);
	}

	[Fact]
	public async Task Singleton_SecondBoardOfMethodologyKind_Rejected()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "spec", "spec");
		// A 2nd spec board is rejected (one-per-project); GuardAsync is not on board_create,
		// so the service throws — assert the message via the service directly.
		var act = () => _tasks.CreateBoardAsync(Proj, "spec2", "spec", null, null);
		(await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*one-per-project*");
	}

	[Fact]
	public async Task Singleton_FreeBoards_Unlimited()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "f1", "free");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "f2", "free");
		Json(await TasksTools.BoardListAsync(http, Flags(), _tasks, Proj)).GetProperty("boards")
			.EnumerateArray().Count().Should().Be(2);
	}

	static IHttpContextAccessor Http(string scopes)
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", scopes)], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(id) } };
	}

	static FeatureFlags Flags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}

	static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
	static JsonElement Json(object? o) => JsonSerializer.SerializeToElement(o, CamelCase);
}
