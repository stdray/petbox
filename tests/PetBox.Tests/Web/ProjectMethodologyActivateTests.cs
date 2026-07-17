using LinqToDB;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Auth;
using PetBox.Web.Pages.Admin;

namespace PetBox.Tests.Web;

// spec methodology-switch: "select/create a target instance -> activate" as ONE guided act, on
// the human-facing Methodology admin page (the MCP-only path already existed via
// tasks_methodology_create + tasks_methodology_set_active — this pins the UI's Activate button,
// OnPostActivateAsync). The hard boundary carried over unchanged from methodology-active-instance:
// switching NEVER closes/destroys whichever instance was active before — retiring an instance
// stays tasks_methodology_close, a separate explicit act.
public sealed class ProjectMethodologyActivateTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public ProjectMethodologyActivateTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-methactivate-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		var store = new TaskBoardStore(_db.Factory(), _factory);
		_tasks = new TasksService(store, new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{ ["Features:Tasks"] = "true" }).Build());

	ProjectMethodologyModel Page() =>
		new(new ProjectDirectory(_db.Factory()), Flags(), _tasks, new WorkspaceMembershipService(_db.Factory()))
		{ WorkspaceKey = "ws", ProjectKey = Proj };

	[Fact]
	public async Task Activate_SetsPointer_WithoutClosingEitherInstance()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "alpha", "builtin", "classic");
		await _tasks.CreateMethodologyInstanceAsync(Proj, "beta", "builtin", "classic");
		await _tasks.SetActiveMethodologyInstanceAsync(Proj, "alpha", 0);

		var page = Page();
		await page.OnGetAsync(default); // loads ActiveVersion baseline
		var result = await page.OnPostActivateAsync("beta", page.ActiveVersion, default);

		result.Should().BeOfType<RedirectToPageResult>();

		var pointer = await _tasks.GetActiveMethodologyInstanceAsync(Proj);
		pointer.Name.Should().Be("beta");

		// The switch's hard boundary: neither instance was closed by the switch itself.
		(await _tasks.GetMethodologyInstanceAsync(Proj, "alpha"))!.Closed.Should().BeFalse(
			"switch must never implicitly destroy the previously active instance — retire is a separate explicit act");
		(await _tasks.GetMethodologyInstanceAsync(Proj, "beta"))!.Closed.Should().BeFalse();
	}

	[Fact]
	public async Task Activate_StaleVersion_RejectsWithMessage_PointerUnchanged()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "alpha", "builtin", "classic");
		await _tasks.SetActiveMethodologyInstanceAsync(Proj, "alpha", 0);
		var staleBaseline = (await _tasks.GetActiveMethodologyInstanceAsync(Proj)).Version;

		await _tasks.CreateMethodologyInstanceAsync(Proj, "beta", "builtin", "classic");
		// Move the pointer once behind the page's back, so the page's stale baseline conflicts.
		await _tasks.SetActiveMethodologyInstanceAsync(Proj, "beta", staleBaseline);

		var page = Page();
		var result = await page.OnPostActivateAsync("alpha", staleBaseline, default);

		result.Should().BeOfType<PageResult>();
		page.ErrorMessage.Should().Contain("stale");
		(await _tasks.GetActiveMethodologyInstanceAsync(Proj)).Name.Should().Be("beta",
			"a rejected activate must not silently overwrite the pointer");
	}
}
