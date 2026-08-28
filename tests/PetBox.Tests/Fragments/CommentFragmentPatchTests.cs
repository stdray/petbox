using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Fragments;

// comments_upsert `fragment` (work/write-fragment-patch). Same contract as tasks_upsert's, proved
// separately because it is a DIFFERENT merge site: CommentService resolves against `currentById`,
// its own read of the active row, not TasksService's `prior`. Doing tasks only and assuming the
// other two follow is exactly the copy-paste-later failure the card rejected.
public sealed class CommentFragmentPatchTests : IDisposable
{
	const string Proj = "p";
	const string Board = "ideas";
	const string Node = "node1";
	readonly string _dir;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly CommentService _svc;

	public CommentFragmentPatchTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-fragcomment-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
		_factory.GetDb(Proj);
		_svc = new CommentService(_factory);
	}

	public void Dispose()
	{
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	const string Original = "## Finding\n\nthe first point\n\nthe second point\n\nthe third point";

	static FragmentEdit E(string? old, string? @new) => new(old, @new);

	async Task<(string Id, long Version)> SeedAsync(string body = Original)
	{
		var add = await _svc.AddAsync(Proj, Board, Node, null, "alice", body, null);
		add.Applied.Should().BeTrue();
		var view = (await _svc.ListForNodeAsync(Proj, Board, Node)).Single(c => c.Id == add.Id);
		return (view.Id, view.Version);
	}

	async Task<CommentView> ReadAsync(string id) =>
		(await _svc.ListForNodeAsync(Proj, Board, Node)).Single(c => c.Id == id);

	Task<CommentBatchResult> UpsertAsync(params CommentItem[] items) =>
		_svc.UpsertAsync(Proj, Board, items, atomic: true);

	static CommentItem Patch(string id, long version, string? body = null, IReadOnlyList<FragmentEdit>? fragment = null) =>
		new(id, null, null, null, body, null, version, fragment);

	// ── CONTROL ──────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task UniqueFragment_PatchesTheComment()
	{
		var (id, v) = await SeedAsync();

		var r = await UpsertAsync(Patch(id, v, fragment: [E("the second point", "THE SECOND POINT")]));

		r.Applied.Should().BeTrue();
		(await ReadAsync(id)).Body.Should().Be(Original.Replace("the second point", "THE SECOND POINT", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Fragment_PreservesTheAuthorAndTags_LikeAnOrdinaryPatch()
	{
		var add = await _svc.AddAsync(Proj, Board, Node, null, "alice", Original, ["keep:me"]);
		var v = (await ReadAsync(add.Id!)).Version;

		await UpsertAsync(Patch(add.Id!, v, fragment: [E("the first point", "1st")]));

		var c = await ReadAsync(add.Id!);
		c.Author.Should().Be("alice");
		c.Tags.Should().BeEquivalentTo(new[] { "keep:me" });
		c.Body.Should().Contain("1st");
	}

	// ── refusals, each with a read-back ──────────────────────────────────────────────

	[Fact]
	public async Task FragmentMatchingTwice_IsRefused_BodyUnchanged()
	{
		var (id, v) = await SeedAsync("say AGAIN and say AGAIN");

		var r = await UpsertAsync(Patch(id, v, fragment: [E("say AGAIN", "X")]));

		r.Applied.Should().BeFalse();
		var c = r.Conflicts.Should().ContainSingle().Subject;
		c.Id.Should().Be(id);
		c.Kind.Should().Be("Rejected");
		c.Reason.Should().Contain("occurs 2 times");

		var stored = await ReadAsync(id);
		stored.Body.Should().Be("say AGAIN and say AGAIN");
		stored.Version.Should().Be(v);
	}

	[Fact]
	public async Task FragmentMatchingZeroTimes_IsRefused_BodyUnchanged()
	{
		var (id, v) = await SeedAsync();

		var r = await UpsertAsync(Patch(id, v, fragment: [E("nowhere in this text", "X")]));

		r.Applied.Should().BeFalse();
		r.Conflicts.Should().ContainSingle().Which.Reason.Should().Contain("does not occur");

		var stored = await ReadAsync(id);
		stored.Body.Should().Be(Original);
		stored.Version.Should().Be(v);
	}

	[Fact]
	public async Task MultiEditList_IsAllOrNothing()
	{
		var (id, v) = await SeedAsync();

		var ok = await UpsertAsync(Patch(id, v, fragment: [E("the first point", "1st"), E("the third point", "3rd")]));
		ok.Applied.Should().BeTrue();
		var after = await ReadAsync(id);
		after.Body.Should().Contain("1st").And.Contain("3rd");

		// ...and now a list whose LAST edit fails: the first two must not survive.
		var before = after.Body;
		var bad = await UpsertAsync(Patch(id, after.Version,
			fragment: [E("1st", "one"), E("the second point", "2nd"), E("MISSING", "x")]));

		bad.Applied.Should().BeFalse();
		bad.Conflicts.Should().ContainSingle().Which.Reason.Should().Contain("fragment[2]");
		(await ReadAsync(id)).Body.Should().Be(before);
	}

	[Fact]
	public async Task BodyAndFragmentTogether_IsRefused()
	{
		var (id, v) = await SeedAsync();

		var r = await UpsertAsync(Patch(id, v, body: "replacement", fragment: [E("the first point", "1st")]));

		r.Applied.Should().BeFalse();
		r.Conflicts.Should().ContainSingle().Which.Reason.Should().Contain("mutually exclusive");
		(await ReadAsync(id)).Body.Should().Be(Original);
	}

	[Fact]
	public async Task FragmentOnACreate_IsRefused_NoCommentIsAdded()
	{
		// A create (no id) has no current text, so there is nothing for `old` to match.
		var r = await UpsertAsync(new CommentItem(null, Node, null, "bob", null, null, 0, [E("x", "y")]));

		r.Applied.Should().BeFalse();
		var c = r.Conflicts.Should().ContainSingle().Subject;
		c.Id.Should().Be("#0");                     // a create has no id — it is named by position
		c.Reason.Should().Contain("has no text to match");
		(await _svc.ListForNodeAsync(Proj, Board, Node)).Should().BeEmpty();
	}

	[Fact]
	public async Task StaleBaseline_StillWins_OverAMatchingFragment()
	{
		var (id, v) = await SeedAsync();
		await UpsertAsync(Patch(id, v, body: "someone else rewrote this"));

		var r = await UpsertAsync(Patch(id, v, fragment: [E("the first point", "1st")]));

		r.Applied.Should().BeFalse();
		// The text moved AND the baseline is behind; either way the write must not land.
		(await ReadAsync(id)).Body.Should().Be("someone else rewrote this");
	}

	[Fact]
	public async Task Partial_ABadFragmentIsRejectedPerItem_TheGoodOneLands()
	{
		var (bad, badV) = await SeedAsync();
		var (good, goodV) = await SeedAsync("a separate comment");

		var r = await _svc.UpsertAsync(Proj, Board,
			[
				Patch(bad, badV, fragment: [E("MISSING", "x")]),
				Patch(good, goodV, fragment: [E("separate", "distinct")]),
			],
			atomic: false);

		r.Applied.Should().BeTrue();
		r.Conflicts.Should().ContainSingle().Which.Id.Should().Be(bad);
		(await ReadAsync(bad)).Body.Should().Be(Original);
		(await ReadAsync(good)).Body.Should().Be("a distinct comment");
	}
}
