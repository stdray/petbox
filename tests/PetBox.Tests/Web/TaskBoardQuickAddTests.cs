using LinqToDB;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Pages.ProjectHome;

namespace PetBox.Tests.Web;

// Regression for the kinded-board quick-add bug: the board page's quick-add used to hardcode
// status "Pending", which is invalid on a kinded board (ideas/spec/intake) and stranded the
// node outside its FSM. It must use the board kind's initial status + node type.
[Collection("DataModule")]
public sealed class TaskBoardQuickAddTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly TasksService _tasks;

	public TaskBoardQuickAddTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-quickadd-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		MigrationRunner.Run(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = "proj", WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db, _factory);
		_tasks = new TasksService(_store, new RelationStore(_db), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{ ["Features:Tasks"] = "true" }).Build());

	async Task<PlanNode> QuickAdd(string board, string kind)
	{
		await _store.CreateAsync("proj", board, null, kind);
		var model = new TaskBoardModel(Flags(), _tasks, new CommentService(_factory)) { WorkspaceKey = "ws", ProjectKey = "proj", Board = board };
		await model.OnPostCreateAsync("My item", "details", 50, default);
		return _store.GetContext("proj").PlanNodes.Where(n => n.Board == board && n.ActiveTo == null).ToList().Single();
	}

	[Fact]
	public async Task QuickAdd_OnIdeasBoard_UsesInitialStatusAndType()
	{
		var n = await QuickAdd("brain", "ideas");
		n.Status.Should().Be("raw");   // ideas initial, NOT "Pending"
		n.Type.Should().Be("idea");
		n.NodeId.Should().NotBeNullOrEmpty(); // linkable (the direct write must still assign a NodeId)
	}

	[Fact]
	public async Task QuickAdd_OnSpecBoard_UsesDraft()
	{
		var n = await QuickAdd("spec", "spec");
		n.Status.Should().Be("draft");
		n.Type.Should().Be("spec");
	}

	[Fact]
	public async Task QuickAdd_OnFreeBoard_StaysPending()
	{
		var n = await QuickAdd("scratch", "free");
		n.Status.Should().Be("Pending"); // free boards keep the legacy default
		n.Type.Should().BeEmpty();
	}
}
