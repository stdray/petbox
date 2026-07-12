using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Search;

namespace PetBox.Tests.Web;

// REPRO (prod bug, GET /ui/search?q=llm-l5 -> 500): the cross-scope fan-out
// (CrossScopeTaskSearchService, up to MaxProjectConcurrency=6 branches in parallel) runs inside
// ONE request scope against ONE scoped ITasksService, whose TaskBoardStore holds ONE scoped
// PetBoxDb — a LinqToDB DataConnection, which is NOT thread-safe. Every full-text branch that
// produces hits calls _boards.FindAsync (TasksService.cs:1457 -> TaskBoardStore.cs:96) on that
// shared connection; overlapping calls corrupt the command's parameter list, which is why prod
// threw "Must add values for the following parameters: @projectKey, @board" — precisely
// FindAsync's two parameters.
//
// The embed leg is what ALIGNS the branches in prod (the trace shows 8 CapabilityRouter embed
// calls, then the throw), so the fixture uses a deliberately slow stub embedder to reproduce
// that alignment instead of relying on luck.
public sealed class CrossScopeSearchConcurrencyReproTests : IDisposable
{
	const int Projects = 8;

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public CrossScopeSearchConcurrencyReproTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-xscope-race-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		for (var i = 0; i < Projects; i++)
			_db.Insert(new Project { Key = $"proj-{i}", WorkspaceKey = "ws1", Name = $"P{i}", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		var store = new TaskBoardStore(_db, _factory);
		_tasks = new TasksService(store, new RelationStore(_factory), new TagStore(_factory),
			new CommentService(_factory), new SlowStubEmbedder());
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public async Task FanOut_FullTextHitsInManyProjects_DoesNotRaceOnTheSharedPetBoxDb()
	{
		// Every project holds a node matching the query by FULL TEXT (not by slug), so no branch
		// short-circuits on the exact-identifier leg: all of them reach HybridCandidatesAsync.
		for (var i = 0; i < Projects; i++)
			await _tasks.UpsertAsync($"proj-{i}", "intake", [
				new NodePatch { Key = $"note-{i}", Title = "Router capability llm l5 rollout", Body = "llm l5 tier notes" }]);

		var scope = new Dictionary<string, IReadOnlyList<Project>>(StringComparer.Ordinal)
		{
			["ws1"] = Enumerable.Range(0, Projects)
				.Select(i => new Project { Key = $"proj-{i}", WorkspaceKey = "ws1", Name = $"P{i}", Description = "" })
				.ToList(),
		};

		var svc = new CrossScopeTaskSearchService(nav: null!, http: null!, tasks: _tasks);

		var act = async () =>
		{
			for (var attempt = 0; attempt < 5; attempt++)
				await svc.SearchAsync(scope, "llm", "https", "box.test");
		};

		await act.Should().NotThrowAsync(
			"the cross-scope fan-out must not use one scoped PetBoxDb from several threads at once");
	}

	// Mimics the prod embedder's latency so every fan-out branch finishes its embed at roughly the
	// same moment and then races into the shared PetBoxDb.
	sealed class SlowStubEmbedder : ILlmClient
	{
		public async Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default)
		{
			await Task.Delay(25, ct);
			return new EmbedResult(
				request.Inputs.Select(i => { var v = new float[1024]; v[Math.Abs(i.GetHashCode()) % 1024] = 1f; return v; }).ToList(),
				new ModelIdentity("fake-embed-v1", 1024), new ServedBy("fake", "fake-embed-v1", 1, Degraded: false));
		}

		public Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();

		public Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default) =>
			throw new NotSupportedException();

		public Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default) =>
			Task.FromResult(true);
	}
}
