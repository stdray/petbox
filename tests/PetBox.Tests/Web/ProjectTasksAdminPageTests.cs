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
}
