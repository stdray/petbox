using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// A comment's optional SLUG — its human-readable address within its owning node (work
// `comment-slug-and-refs`, spec `comment-addressable`). Driven straight through CommentService, the
// door comments_upsert goes through, because every rule here is a service rule: the DB index is a
// lookup, not the invariant (see M025_CommentSlug for why it is deliberately not unique).
//
// The four properties under test are the ones the feature would be worthless without:
//   * the scope of uniqueness is the NODE, not the board and not the project;
//   * the address is WRITE-ONCE — the decision this card had to make, and the reason it was made
//     this way is silent breakage: re-pointing a slug turns every `[[#slug]]` that quotes it into
//     plain text, with nothing raised anywhere. Refusing the change removes the class outright;
//   * a slug is OPTIONAL and absence is permanent-normal — every comment written before this field
//     existed has none and must keep round-tripping untouched;
//   * a refusal is NAMED — an aborted atomic batch (the throw its sibling guards use) or a per-item
//     conflict in partial mode — never a clobber, and never a silent no-op that drops the field.
public sealed class CommentSlugTests : IDisposable
{
	const string Proj = "p";
	const string Board = "ideas";
	const string NodeA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
	const string NodeB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

	readonly string _dir;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly CommentService _svc;

	public CommentSlugTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-comment-slug-" + Guid.NewGuid().ToString("N"));
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

	static CommentItem Create(string nodeId, string body, string? slug = null) =>
		new(null, nodeId, null, "author", body, null, 0, Slug: slug);

	Task<CommentBatchResult> Upsert(bool atomic, params CommentItem[] items) =>
		_svc.UpsertAsync(Proj, Board, items, atomic);

	// A PATCH under the watermark the last write echoed (the `version` contract, same as every other
	// caller of this door). `slug` follows CommentItem's own null/""/value rule.
	static CommentItem Patch(string id, string? body, long version, string? slug = null) =>
		new(id, null, null, null, body, null, version, Slug: slug);

	// The refusal channel of an ATOMIC batch is the throw its sibling guards already use ("body is
	// required", "parentId is not an active comment"): the call is aborted, nothing is written, and
	// McpErrorEnvelopeFilter renders the message. A PARTIAL batch turns the same refusal into a
	// per-item conflict instead — pinned by PartialMode_DuplicateSlug_RefusesOnlyThatItem below.
	async Task<string> Refused(params CommentItem[] items)
	{
		var act = async () => await Upsert(true, items);
		return (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
	}

	// ── the address itself ───────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task Create_WithSlug_RoundTrips_OnEveryReadDoor()
	{
		var r = await Upsert(true, Create(NodeA, "the analysis", "part-04"));

		r.Applied.Should().BeTrue();
		r.Added.Single().Slug.Should().Be("part-04");

		var id = r.Added.Single().Id;
		(await _svc.GetAsync(Proj, id))!.Slug.Should().Be("part-04");
		(await _svc.ListForNodeAsync(Proj, Board, NodeA)).Single().Slug.Should().Be("part-04");
	}

	[Fact]
	public async Task Create_WithoutSlug_HasNone_AndThatIsNormal()
	{
		var r = await Upsert(true, Create(NodeA, "no address needed"));

		r.Applied.Should().BeTrue();
		r.Added.Single().Slug.Should().BeNull("absence is the ordinary state, not a gap to be filled");
	}

	[Fact]
	public async Task Slug_IsNormalized_TrimmedAndLowercased()
	{
		var r = await Upsert(true, Create(NodeA, "x", "  Part-04  "));

		r.Added.Single().Slug.Should().Be("part-04", "same normalization a node key gets (TaskSlug)");
	}

	[Theory]
	[InlineData("4-part")]      // must start a-z
	[InlineData("part 04")]     // no spaces
	[InlineData("part/04")]     // a single flat segment
	[InlineData("частьЧетыре")] // ascii only, like every other slug in the product
	public async Task Slug_WithInvalidShape_IsRefused(string slug)
	{
		(await Refused(Create(NodeA, "x", slug))).Should().Contain("not a valid comment slug");

		(await _svc.ListForNodeAsync(Proj, Board, NodeA)).Should().BeEmpty("an atomic batch writes nothing when it refuses");
	}

	// ── uniqueness is scoped to the OWNING NODE ─────────────────────────────────────────────────

	[Fact]
	public async Task TwoComments_UnderDifferentNodes_MayCarryTheSameSlug()
	{
		var a = await Upsert(true, Create(NodeA, "segment of article A", "intro"));
		var b = await Upsert(true, Create(NodeB, "segment of article B", "intro"));

		a.Applied.Should().BeTrue();
		b.Applied.Should().BeTrue("uniqueness is per NODE — two articles both having an `intro` segment is the "
			+ "normal case, and a board-wide or project-wide namespace would make the address unusable");
		(await _svc.ListForNodeAsync(Proj, Board, NodeA)).Single().Slug.Should().Be("intro");
		(await _svc.ListForNodeAsync(Proj, Board, NodeB)).Single().Slug.Should().Be("intro");
	}

	[Fact]
	public async Task DuplicateSlug_UnderTheSameNode_IsRefused_NamingTheHolder()
	{
		var first = await Upsert(true, Create(NodeA, "the first", "intro"));
		var holder = first.Added.Single().Id;

		var message = await Refused(Create(NodeA, "the second", "intro"));

		message.Should().Contain("already used by comment").And.Contain(holder,
			"a refusal that does not name the holder leaves the caller guessing which segment owns the address");
		(await _svc.ListForNodeAsync(Proj, Board, NodeA)).Should().HaveCount(1, "the refused create did not land");
	}

	[Fact]
	public async Task DuplicateSlug_WithinOneBatch_IsRefused_NotSilentlyBothStored()
	{
		var message = await Refused(Create(NodeA, "one", "intro"), Create(NodeA, "two", "intro"));

		message.Should().Contain("already used by comment",
			"the collision is between two items of THIS call — a batch-local claim, which a check "
			+ "against stored rows alone would miss entirely");
		(await _svc.ListForNodeAsync(Proj, Board, NodeA)).Should().BeEmpty("nothing was written");
	}

	[Fact]
	public async Task PartialMode_DuplicateSlug_RefusesOnlyThatItem()
	{
		await Upsert(true, Create(NodeA, "the first", "intro"));

		var r = await Upsert(false, Create(NodeA, "the second", "intro"), Create(NodeA, "unrelated", "outro"));

		r.Applied.Should().BeTrue();
		r.Added.Should().ContainSingle().Which.Slug.Should().Be("outro");
		r.Conflicts.Should().ContainSingle().Which.Id.Should().Be("#0", "a rejected CREATE has no id yet");
	}

	// ── WRITE-ONCE ───────────────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task Slug_MayBeGiven_ToACommentThatHasNone()
	{
		var created = await Upsert(true, Create(NodeA, "written before slugs existed"));
		var id = created.Added.Single().Id;

		var patched = await Upsert(true, Patch(id, "written before slugs existed", created.CurrentVersion, "part-04"));

		patched.Applied.Should().BeTrue("giving an address to something that had none is not a re-point");
		(await _svc.GetAsync(Proj, id))!.Slug.Should().Be("part-04");
	}

	[Fact]
	public async Task ChangingAnExistingSlug_IsRefused()
	{
		var created = await Upsert(true, Create(NodeA, "body", "part-04"));
		var id = created.Added.Single().Id;

		var message = await Refused(Patch(id, "body", created.CurrentVersion, "part-13"));

		message.Should().Contain("write-once");
		(await _svc.GetAsync(Proj, id))!.Slug.Should().Be("part-04", "the stored address never moved");
	}

	[Fact]
	public async Task ClearingAnExistingSlug_IsRefused()
	{
		var created = await Upsert(true, Create(NodeA, "body", "part-04"));
		var id = created.Added.Single().Id;

		var message = await Refused(Patch(id, "body", created.CurrentVersion, ""));

		message.Should().Contain("write-once",
			"a clear is a re-point with extra steps — allowing it would reopen the silent-breakage "
			+ "hole through the back door (clear, then set)");
		(await _svc.GetAsync(Proj, id))!.Slug.Should().Be("part-04");
	}

	[Fact]
	public async Task ResubmittingTheSameSlug_IsANoOp_NotARefusal()
	{
		var created = await Upsert(true, Create(NodeA, "body", "part-04"));
		var id = created.Added.Single().Id;

		var r = await Upsert(true, Patch(id, "a new body", created.CurrentVersion, "part-04"));

		r.Applied.Should().BeTrue("a declarative writer resends the whole item; asking for the address it "
			+ "already has is not a change");
		(await _svc.GetAsync(Proj, id))!.Body.Should().Be("a new body");
	}

	// ── a comment with no slug keeps working, everywhere ────────────────────────────────────────

	[Fact]
	public async Task BodyPatch_OmittingSlug_LeavesItAlone_InBothDirections()
	{
		var withSlug = (await Upsert(true, Create(NodeA, "one", "part-04"))).Added.Single().Id;
		var second = await Upsert(true, Create(NodeA, "two"));
		var without = second.Added.Single().Id;

		var r = await Upsert(true,
			Patch(withSlug, "one edited", second.CurrentVersion),
			Patch(without, "two edited", second.CurrentVersion));

		r.Applied.Should().BeTrue();
		(await _svc.GetAsync(Proj, withSlug))!.Slug.Should().Be("part-04", "omitted means inherit, not clear");
		(await _svc.GetAsync(Proj, without))!.Slug.Should().BeNull("omitted never invents one either");
	}

	[Fact]
	public async Task UiEditDoor_PreservesTheSlug()
	{
		var created = await Upsert(true, Create(NodeA, "body", "part-04"));
		var id = created.Added.Single().Id;
		var version = created.CurrentVersion;

		// EditAsync is the low-ceremony door the node page's inline editor uses. It carries identity
		// forward with `cur with { Body = … }`, so the slug rides along — this pins that it does,
		// because a human editing a typo must not silently drop the address bodies are quoting.
		(await _svc.EditAsync(Proj, Board, id, "body edited in the UI", null, version)).Applied.Should().BeTrue();

		(await _svc.GetAsync(Proj, id))!.Slug.Should().Be("part-04");
	}

	[Fact]
	public async Task FragmentPatch_CarriesTheSlug_AndRefusesAChangeThroughConflicts()
	{
		var created = await Upsert(true, Create(NodeA, "the original text", "part-04"));
		var id = created.Added.Single().Id;

		var ok = await Upsert(true, new CommentItem(id, null, null, null, null, null, created.CurrentVersion,
			Fragment: [new FragmentEdit("original", "edited")]));
		ok.Applied.Should().BeTrue();
		(await _svc.GetAsync(Proj, id))!.Slug.Should().Be("part-04");

		var refused = await Upsert(true, new CommentItem(id, null, null, null, null, null, ok.CurrentVersion,
			Fragment: [new FragmentEdit("edited", "edited again")], Slug: "part-13"));
		refused.Applied.Should().BeFalse("a slug riding a fragment edit is judged by the same rule — "
			+ "silently ignoring the field would be the one unacceptable option");
		refused.Conflicts.Single().Reason.Should().Contain("write-once",
			"this branch words every refusal into conflicts[] in BOTH modes (its own long-standing rule), "
			+ "unlike the ordinary body branch, which throws on an atomic batch");
	}

	[Fact]
	public async Task DeletedComment_ReleasesItsSlug_ForANewOne()
	{
		var created = await Upsert(true, Create(NodeA, "first draft of the segment", "part-04"));
		var id = created.Added.Single().Id;

		(await _svc.DeleteAsync(Proj, Board, id)).Should().BeTrue();
		var replacement = await Upsert(true, Create(NodeA, "rewritten segment", "part-04"));

		replacement.Applied.Should().BeTrue("uniqueness is over the ACTIVE comments of the node — this is "
			+ "also the escape hatch from write-once for a comment with no replies");
	}
}
