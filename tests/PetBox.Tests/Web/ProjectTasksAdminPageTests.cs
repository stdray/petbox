using LinqToDB;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Auth;
using PetBox.Web.Pages.Admin;

namespace PetBox.Tests.Web;

// namespace-create-in-ui: the admin tasks page's "Simple board" create control
// (board-create-form / board-create-name / board-create-submit) is the human-facing
// equivalent of the MCP tasks_board_create door that a parallel gate change is closing
// off for unknown-name auto-create. Prove the POST handler behind it actually creates
// the board (ModuleViewsTests.TasksAdmin_RendersCreateForm_AndListsBoard already covers
// that the form renders).
public sealed class ProjectTasksAdminPageTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly TasksService _tasks;

	public ProjectTasksAdminPageTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-tasksadmin-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db.Factory(), _factory);
		_tasks = new TasksService(_store, new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static FeatureFlags Features() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{ ["Features:Tasks"] = "true" }).Build());

	ProjectTasksModel Page() =>
		new(new ProjectDirectory(_db.Factory()), Features(), _tasks) { WorkspaceKey = "ws", ProjectKey = Proj };

	[Fact]
	public async Task Create_NewSimpleBoard_CreatesAndRedirects()
	{
		var result = await Page().OnPostCreateAsync("roadmap", "the plan", CancellationToken.None);

		result.Should().BeOfType<RedirectToPageResult>();
		var boards = await _tasks.ListBoardsAsync(Proj, CancellationToken.None);
		boards.Should().ContainSingle(b => b.Name == "roadmap");
	}

	// spec methodology-inactive-visibility: with exactly one open instance and no active
	// pointer, resolution is unambiguous (that instance IS the effective default) — the page
	// must compute EffectiveActiveInstance to match it, so its own boards never show "not
	// active". This is the live $system shape today (single open instance, no pointer set) —
	// the invariant the worker's prediction leans on.
	[Fact]
	public async Task SingleOpenInstance_NoPointer_IsTheEffectiveDefault_NotInactive()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "quartet", "builtin", "quartet");

		var page = Page();
		await page.OnGetAsync(CancellationToken.None);

		page.EffectiveActiveInstance.Should().Be("quartet");
		var quartetBoards = page.Boards.Where(b => b.MethodologyInstance == "quartet").ToList();
		quartetBoards.Should().NotBeEmpty();
		quartetBoards.Should().OnlyContain(b => b.MethodologyInstance == page.EffectiveActiveInstance);
	}

	// Two open instances, no pointer set: an EXPLICIT ambiguous state (spec
	// methodology-active-instance) — EffectiveActiveInstance is null, so EVERY board with
	// instance membership computes as "not active" (no default exists to match).
	[Fact]
	public async Task TwoOpenInstances_NoPointer_NoDefault_BothInstancesComputeInactive()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "alpha", "builtin", "classic");
		await _tasks.CreateMethodologyInstanceAsync(Proj, "beta", "builtin", "classic");

		var page = Page();
		await page.OnGetAsync(CancellationToken.None);

		page.EffectiveActiveInstance.Should().BeNull();
		page.Boards.Where(b => b.MethodologyInstance is not null)
			.Should().OnlyContain(b => !string.Equals(b.MethodologyInstance, page.EffectiveActiveInstance));
	}

	// Once a pointer picks "beta", beta's boards match the effective default and alpha's do not
	// — the exact comparison the Razor badge renders off.
	[Fact]
	public async Task TwoOpenInstances_PointerSet_OnlyThatInstanceMatchesDefault()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "alpha", "builtin", "classic");
		await _tasks.CreateMethodologyInstanceAsync(Proj, "beta", "builtin", "classic");
		await _tasks.SetActiveMethodologyInstanceAsync(Proj, "beta", 0);

		var page = Page();
		await page.OnGetAsync(CancellationToken.None);

		page.EffectiveActiveInstance.Should().Be("beta");
		page.Boards.Where(b => b.MethodologyInstance == "beta")
			.Should().OnlyContain(b => b.MethodologyInstance == page.EffectiveActiveInstance);
		page.Boards.Where(b => b.MethodologyInstance == "alpha")
			.Should().OnlyContain(b => b.MethodologyInstance != page.EffectiveActiveInstance);
	}
}
