using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// The one invariant that holds MethodologyEngineContext together (methodology-engine-extraction,
// slice 3): the context is assembled ONCE, before the partial-mode retry loop, out of the patches
// AS SUBMITTED plus `prior` — the two inputs the loop never narrows. The loop narrows `live` (and
// the `desired` derived from it), so a context sourced from `live` would judge pass 2 on a smaller
// world than pass 1 while still calling itself "prefetched once".
//
// This is a PARITY test, not a guard test: the guards it fires already have their own coverage.
// What it pins is that their INPUT survives a retry. It lives here rather than in
// PetBox.Tasks.Engine.Tests on purpose — the bug is in the SERVICE half (what gets fetched into
// the context), which the pure engine, by construction, cannot see or express.
public sealed class EngineContextStabilityTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly TasksService _tasks;

	public EngineContextStabilityTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-enginectx-" + Guid.NewGuid().ToString("N"));
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

	static NodePatch Node(string key, string? partOf = null, string? type = null) => new()
	{
		Key = key,
		Title = key.ToUpperInvariant(),
		Body = "body of " + key,
		Type = type,
		PartOf = partOf,
	};

	// A node the workflow refuses outright — the cheapest way to force the retry loop to spin a
	// second pass (`zzz` is not a legal type on a simple board).
	static NodePatch Bad(string key) => Node(key, type: "zzz");

	static NodePatch Delete(string key) => new() { Key = key, Deleted = true };

	[Fact]
	public async Task PartialRetry_ContextKeepsTheDeletesTheLoopNeverSaw_ParentWithChildStillRefused()
	{
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("parent"), Node("child", partOf: "parent"), Node("bystander") });

		// One partial call that does BOTH things the invariant sits between:
		//   `bad`    — refused inside the loop, so `live` narrows and the engine is re-run. This is
		//              the only reason this test differs from the plain delete-guard coverage: it
		//              proves the context outlives a pass, instead of being read once and used once.
		//   `parent` — a DELETE. Deletes are not in `live` AT ALL (`live = upsertPatches`), so the
		//              delete guard's data (PartOfChildrenByNodeId) exists only because the context
		//              was built from `nodes`. Source it from `live` and the guard silently sees an
		//              empty child map, waves the delete through, and orphans `child`.
		//   `ok`     — a survivor, to keep the call a genuine partial apply rather than a total refusal.
		var r = await _tasks.UpsertAsync(Proj, "b",
			new[] { Bad("bad"), Delete("parent"), Node("ok") }, actor: null, atomic: false);

		r.Result.Applied.Should().BeTrue();
		r.Result.Added.Select(n => n.Key).Should().Equal("ok"); // the retry did re-run and land the survivor

		var byKey = r.Result.Conflicts.ToDictionary(c => c.Key, c => c);
		byKey.Keys.Should().BeEquivalentTo("bad", "parent");
		byKey["bad"].Reason.Should().Contain("invalid type 'zzz'");  // the refusal that spent a pass
		byKey["parent"].Kind.Should().Be(TemporalConflictKind.Rejected);
		byKey["parent"].Reason.Should().Contain("active part_of children"); // the verdict that needs `nodes`

		// The load-bearing assertion: `parent` is STILL THERE. If the context is ever rebuilt from
		// the narrowed set, this is what breaks — quietly, with `applied:true` and no conflict at all.
		var ctx = _store.GetContext(Proj);
		ctx.PlanNodes.Any(n => n.Key == "parent" && n.ActiveTo == null).Should().BeTrue();
		ctx.PlanNodes.Any(n => n.Key == "child" && n.ActiveTo == null).Should().BeTrue(); // never orphaned
	}
}
