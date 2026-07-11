using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// Service-level coverage of the informed-Stale contract (intake stale-baseline-blind-retry):
// the repro class was "an FSM effect / concurrent writer already made this change, the agent
// resubmits it on an old watermark and gets a Stale whose only exit is a blind retry". With
// payload arbitration in the store + read-merge in the service, that class collapses to a
// no-op — and a GENUINE semantic race answers with the moved fields of THIS node.
public sealed class StaleBaselineResolveTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public StaleBaselineResolveTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-staleres-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		var store = new TaskBoardStore(_db, _factory);
		_tasks = new TasksService(store, new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static NodePatch Patch(string key, long version = 0, string? status = null,
		string? title = null, string? body = null) => new()
		{ Key = key, Version = version, Status = status, Title = title, Body = body };

	// The repro: the change the author carries ALREADY landed (an effect or a concurrent
	// writer got there first). A status-only patch read-merges against the current row, so
	// the desired payload is identical — no-op instead of Stale + blind retry.
	[Fact]
	public async Task ResubmitOfAlreadyLandedStatus_OnOldWatermark_IsNoOp_NotStale()
	{
		var seed = await _tasks.UpsertAsync(Proj, "b", [Patch("n1", title: "N1", body: "b1")]);
		var readCursor = seed.Result.CurrentVersion;                    // the author's read

		// Another writer (or an FSM effect) moves n1 past the author's baseline.
		var other = await _tasks.UpsertAsync(Proj, "b", [Patch("n1", version: readCursor, status: "Done")]);
		other.Result.Applied.Should().BeTrue();

		// The author submits the SAME transition on the old watermark.
		var r = await _tasks.UpsertAsync(Proj, "b", [Patch("n1", version: readCursor, status: "Done")]);

		r.Result.Applied.Should().BeTrue();
		r.Result.Conflicts.Should().BeEmpty();
		r.Result.Inserted.Should().Be(0); // no-op: nothing to write, no blind-retry round-trip
	}

	// A GENUINE race (the node moved semantically to something the author does not carry)
	// still conflicts — and the conflict names THIS node's moved fields, so the retry is
	// informed instead of "re-read and resubmit with a bigger number".
	[Fact]
	public async Task GenuineSemanticRace_Conflicts_WithThisNodesMovedFields()
	{
		var seed = await _tasks.UpsertAsync(Proj, "b", [Patch("n1", title: "N1", body: "b1")]);
		var readCursor = seed.Result.CurrentVersion;

		// Concurrent writer: status AND body move on past the author's read.
		var other = await _tasks.UpsertAsync(Proj, "b",
			[Patch("n1", version: readCursor, status: "InProgress", body: "theirs")]);
		other.Result.Applied.Should().BeTrue();

		// The author edits the title on the old watermark (their patch does not even touch
		// the moved fields — the conservative contract still surfaces the race).
		var r = await _tasks.UpsertAsync(Proj, "b", [Patch("n1", version: readCursor, title: "mine")]);

		r.Result.Applied.Should().BeFalse();
		var c = r.Result.Conflicts.Should().ContainSingle().Subject;
		c.Kind.Should().Be(TemporalConflictKind.Stale);
		c.ChangedFields.Should().BeEquivalentTo(["status", "body"]); // entity-scoped facts to rebase on
		c.Reason.Should().Contain("status").And.Contain("body");
	}

	// Bookkeeping interventions that leave the payload as the author read it (A→B→A at the
	// service level) auto-resolve and are reported — visible, never silent.
	[Fact]
	public async Task PayloadUnmovedSinceRead_AutoResolves_AndReportsTheKey()
	{
		var seed = await _tasks.UpsertAsync(Proj, "b", [Patch("n1", title: "N1", body: "b1")]);
		var readCursor = seed.Result.CurrentVersion;                                  // author reads Todo/b1

		var b1 = await _tasks.UpsertAsync(Proj, "b", [Patch("n1", version: readCursor, status: "Done")]);
		b1.Result.Applied.Should().BeTrue();
		var b2 = await _tasks.UpsertAsync(Proj, "b",
			[Patch("n1", version: b1.Result.CurrentVersion, status: "Todo")]);        // back to read-state
		b2.Result.Applied.Should().BeTrue();

		// The author's real edit on the old watermark: nothing semantically moved since
		// their read, so it applies — and the resolution is reported.
		var r = await _tasks.UpsertAsync(Proj, "b", [Patch("n1", version: readCursor, body: "mine")]);

		r.Result.Applied.Should().BeTrue();
		r.Result.AutoResolved.Should().Equal("n1");
		r.Result.Updated.Select(n => n.Key).Should().Contain("n1");
	}
}
