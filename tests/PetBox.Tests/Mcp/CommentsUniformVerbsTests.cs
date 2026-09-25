using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Mcp;

// The comments family on the uniform-entity-verbs matrix (comments_upsert / _search / _delta /
// _get), exercised through the MCP adapter over a real per-project TasksDb (FTS included, so the
// lexical q-search path runs for real). Mirrors tasks/memory: create + patch batch, list = search
// without q, a lexical query, a version-cursor delta, and the addressed single read.
public sealed class CommentsUniformVerbsTests : IDisposable
{
	const string Proj = "proj";
	const string Board = "ideas";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _tasksFactory;
	readonly TasksService _tasks;
	readonly CommentService _comments;

	public CommentsUniformVerbsTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-comments-verbs-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_tasksFactory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
		_comments = new CommentService(_tasksFactory);
		_tasks = new TasksService(new TaskBoardStore(_db.Factory(), _tasksFactory), new RelationStore(_tasksFactory),
			new TagStore(_tasksFactory), _comments);
	}

	public void Dispose()
	{
		_db.Dispose();
		_tasksFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static IHttpContextAccessor Http()
	{
		var id = new ClaimsIdentity(
			[new Claim("project", Proj), new Claim("scopes", "tasks:read,tasks:write")], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) } };
	}

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Features:Tasks"] = "true" }).Build());

	static CommentItemInput Create(string node, string author, string body, string[]? tags = null) =>
		new() { Node = node, Author = author, Body = body, Tags = tags };

	Task<CommentsUpsertResult> Upsert(IHttpContextAccessor http, params CommentItemInput[] items) =>
		CommentTools.UpsertAsync(http, Flags(), _comments, _tasks, Proj, Board, items);

	// A stable NodeId to hang comments on. A 32-hex value passes through node-ref resolution
	// unresolved (uniform-node-refs), so no board node is needed for these thread tests.
	static string NewNode() => Guid.NewGuid().ToString("N");

	// atomic:false on comments (spec batch-write-partial-apply). A comment's parentId must address
	// an ALREADY-ACTIVE comment — an intra-batch forward reference is not expressible — so the
	// cascade degenerates here too: every item is independent. A rejected CREATE has no id yet, so
	// its conflict is keyed by the item's POSITION.
	[Fact]
	public async Task Upsert_Partial_ValidItemLands_InvalidRejectedWithReason()
	{
		var http = Http();
		var node = NewNode();

		var r = await CommentTools.UpsertAsync(http, Flags(), _comments, _tasks, Proj, Board,
			[
				Create(node, "alice", "a real comment"),
				new CommentItemInput { Node = node, Author = "bob", Body = "   " },              // empty body
				new CommentItemInput { Id = "deadbeef", Body = "patch of a ghost", Version = 1 },  // unknown id
			],
			atomic: false);

		r.Applied.Should().BeTrue();
		r.Added.Should().ContainSingle();                                  // the valid one landed
		r.Conflicts.Should().HaveCount(2);
		r.Conflicts.Should().OnlyContain(c => c.Kind == "Rejected");
		r.Conflicts.Single(c => c.Id == "#1").Reason.Should().Contain("body is required");
		r.Conflicts.Single(c => c.Id == "deadbeef").Reason.Should().Contain("not found");

		var list = await CommentTools.SearchAsync(http, Flags(), _comments, _tasks, Proj, board: Board, node: node);
		list.Items.Should().ContainSingle(); // only the valid comment exists
	}

	[Fact]
	public async Task Upsert_WithoutTheFlag_OneInvalidItem_AbortsTheWholeBatch()
	{
		var http = Http();
		var node = NewNode();

		var act = () => Upsert(http,
			Create(node, "alice", "a real comment"),
			new CommentItemInput { Node = node, Author = "bob", Body = "" });

		await act.Should().ThrowAsync<ArgumentException>();
		var list = await CommentTools.SearchAsync(http, Flags(), _comments, _tasks, Proj, board: Board, node: node);
		list.Items.Should().BeEmpty(); // the valid sibling did not land either
	}

	[Fact]
	public async Task Upsert_Create_Then_Patch_EchoesOnlyThisCall()
	{
		var http = Http();
		var node = NewNode();

		var created = await Upsert(http, Create(node, "alice", "first body", ["artifact:plan"]));
		created.Applied.Should().BeTrue();
		created.Added.Should().ContainSingle();
		created.Updated.Should().BeEmpty();
		var id = created.Added.Single().Id;
		created.Added.Single().Tags.Should().Equal("artifact:plan");

		// PATCH the body under the echoed cursor; tags omitted → left as-is.
		var patched = await Upsert(http, new CommentItemInput { Id = id, Body = "edited body", Version = created.CurrentVersion });
		patched.Applied.Should().BeTrue();
		patched.Added.Should().BeEmpty();
		patched.Updated.Should().ContainSingle(c => c.Id == id);       // echo covers ONLY this call
		patched.CurrentVersion.Should().BeGreaterThan(created.CurrentVersion);

		var got = await CommentTools.GetAsync(http, Flags(), _comments, _tasks, Proj, id, bodyLen: -1);
		got.Body.Should().Be("edited body");
		got.Tags.Should().Equal("artifact:plan");                       // survived the tags-omitted patch
	}

	// card comment-slug-only-patch-requires-body: a PATCH that only carries `slug` (or only
	// `tags`) used to fail with "comment body is required" even though the tool promises
	// PATCH semantics (an omitted field stays unchanged). `body` must follow the same
	// omitted-stays-unchanged contract as `tags`/`slug` on a PATCH.
	[Fact]
	public async Task Upsert_Patch_SlugOnly_LeavesBodyAndTagsUnchanged()
	{
		var http = Http();
		var node = NewNode();
		var created = await Upsert(http, Create(node, "alice", "original body", ["artifact:plan"]));
		var id = created.Added.Single().Id;

		var patched = await Upsert(http, new CommentItemInput { Id = id, Slug = "part-one", Version = created.CurrentVersion });
		patched.Applied.Should().BeTrue();
		patched.Updated.Should().ContainSingle(c => c.Id == id && c.Slug == "part-one");

		var got = await CommentTools.GetAsync(http, Flags(), _comments, _tasks, Proj, id, bodyLen: -1);
		got.Body.Should().Be("original body");   // untouched by the slug-only patch
		got.Tags.Should().Equal("artifact:plan"); // untouched too
		got.Slug.Should().Be("part-one");
	}

	[Fact]
	public async Task Upsert_Patch_TagsOnly_LeavesBodyUnchanged()
	{
		var http = Http();
		var node = NewNode();
		var created = await Upsert(http, Create(node, "alice", "original body", ["old-tag"]));
		var id = created.Added.Single().Id;

		var patched = await Upsert(http, new CommentItemInput { Id = id, Tags = ["new-tag"], Version = created.CurrentVersion });
		patched.Applied.Should().BeTrue();
		patched.Updated.Should().ContainSingle(c => c.Id == id);

		var got = await CommentTools.GetAsync(http, Flags(), _comments, _tasks, Proj, id, bodyLen: -1);
		got.Body.Should().Be("original body"); // untouched by the tags-only patch
		got.Tags.Should().Equal("new-tag");
	}

	// card comment-tags-only-patch-keeps-version: prod evidence (f1db66d, smoke/work,
	// 2026-09-23) showed a tags-only PATCH applying (tags landed on read-back) while the
	// comment's Version/Updated and the batch's CurrentVersion all stayed put — invisible to
	// comments_delta's version cursor and to a concurrent writer's stale-baseline CAS check.
	// Root cause: CommentRow.SamePayload never looked at tags (they live in comment_tag, a
	// side association), so TemporalStore classified the row as an identical-payload no-op.
	// The fix threads a TagsFingerprint payload field through the row (M026); this asserts the
	// visible contract a caller actually depends on: a real version bump, and delta visibility.
	[Fact]
	public async Task Upsert_Patch_TagsOnly_BumpsVersion_AndIsVisibleToDelta()
	{
		var http = Http();
		var node = NewNode();
		var created = await Upsert(http, Create(node, "alice", "original body", ["old-tag"]));
		var id = created.Added.Single().Id;
		var baseline = created.CurrentVersion;

		var patched = await Upsert(http, new CommentItemInput { Id = id, Tags = ["new-tag"], Version = baseline });
		patched.Applied.Should().BeTrue();
		var echoed = patched.Updated.Should().ContainSingle(c => c.Id == id).Subject;
		echoed.Version.Should().BeGreaterThan(baseline);
		patched.CurrentVersion.Should().BeGreaterThan(baseline);

		var got = await CommentTools.GetAsync(http, Flags(), _comments, _tasks, Proj, id, bodyLen: -1);
		got.Version.Should().Be(echoed.Version);

		// A caller who read at `baseline` and advanced its cursor there must be handed this
		// tags-only edit on its next delta — the whole point of the version bump.
		var delta = await CommentTools.DeltaAsync(http, Flags(), _comments, Proj, Board, baseline, bodyLen: -1);
		delta.Updated.Should().ContainSingle(c => c.Id == id && c.Tags.SequenceEqual(new[] { "new-tag" }));
	}

	// A stale baseline on a tags-only patch must conflict like any other payload change — the
	// CAS guarantee the version bump above exists to provide. Before the fix this patch would
	// have applied silently (SamePayload saw no change to reject against).
	[Fact]
	public async Task Upsert_Patch_TagsOnly_StaleBaseline_Conflicts_NothingWritten()
	{
		var http = Http();
		var node = NewNode();
		var created = await Upsert(http, Create(node, "alice", "original body", ["old-tag"]));
		var id = created.Added.Single().Id;
		var baseline = created.CurrentVersion;

		// A concurrent writer moves the comment's tags past the author's baseline.
		var other = await Upsert(http, new CommentItemInput { Id = id, Tags = ["their-tag"], Version = baseline });
		other.Applied.Should().BeTrue();

		var stale = await Upsert(http, new CommentItemInput { Id = id, Tags = ["my-tag"], Version = baseline });
		stale.Applied.Should().BeFalse();
		var conflict = stale.Conflicts.Should().ContainSingle(c => c.Id == id).Subject;
		conflict.Kind.Should().Be("Stale");
		conflict.ChangedFields.Should().BeEquivalentTo(["tags"]);

		var got = await CommentTools.GetAsync(http, Flags(), _comments, _tasks, Proj, id, bodyLen: -1);
		got.Tags.Should().Equal("their-tag"); // the stale attempt never landed
	}

	// An identical resubmit (same tags as already active) must stay the no-op it always was —
	// retry safety, not a spurious version bump on every re-send of the same payload. The echo
	// still names the comment in Updated (patchedKeys forces the Added/Updated split for any
	// key that came from an active row — see CommentService.UpsertAsync's `mine` comment), so
	// the no-op signal here is the UNCHANGED version, not absence from the echo.
	[Fact]
	public async Task Upsert_Patch_TagsOnly_IdenticalResend_IsNoOp()
	{
		var http = Http();
		var node = NewNode();
		var created = await Upsert(http, Create(node, "alice", "original body", ["same-tag"]));
		var id = created.Added.Single().Id;
		var baseline = created.CurrentVersion;

		var resend = await Upsert(http, new CommentItemInput { Id = id, Tags = ["same-tag"], Version = baseline });
		resend.Applied.Should().BeTrue();
		resend.Added.Should().BeEmpty();
		var echoed = resend.Updated.Should().ContainSingle(c => c.Id == id).Subject;
		echoed.Version.Should().Be(baseline);         // no new revision minted
		resend.CurrentVersion.Should().Be(baseline);

		var got = await CommentTools.GetAsync(http, Flags(), _comments, _tasks, Proj, id, bodyLen: -1);
		got.Version.Should().Be(baseline);
		got.Tags.Should().Equal("same-tag");
	}

	[Fact]
	public async Task Upsert_Patch_CarryingNoChangedField_IsRefused()
	{
		var http = Http();
		var node = NewNode();
		var created = await Upsert(http, Create(node, "alice", "original body"));
		var id = created.Added.Single().Id;

		// No body, no tags, no slug (fragment/bodyRef untouched) — nothing for this patch to do.
		var act = () => Upsert(http, new CommentItemInput { Id = id, Version = created.CurrentVersion });
		var ex = await act.Should().ThrowAsync<ArgumentException>();
		ex.Which.Message.Should().Contain("no changes");

		var got = await CommentTools.GetAsync(http, Flags(), _comments, _tasks, Proj, id, bodyLen: -1);
		got.Body.Should().Be("original body"); // refused, not silently applied
	}

	[Fact]
	public async Task Upsert_Patch_ExplicitBlankBody_IsRefused_NotTreatedAsAClear()
	{
		var http = Http();
		var node = NewNode();
		var created = await Upsert(http, Create(node, "alice", "original body"));
		var id = created.Added.Single().Id;

		var act = () => Upsert(http, new CommentItemInput { Id = id, Body = "   ", Version = created.CurrentVersion });
		var ex = await act.Should().ThrowAsync<ArgumentException>();
		ex.Which.Message.Should().Contain("blank");
	}

	[Fact]
	public async Task Upsert_Create_WithoutBody_IsStillRefused()
	{
		var http = Http();
		var node = NewNode();

		var act = () => Upsert(http, new CommentItemInput { Node = node, Author = "alice", Body = null });
		var ex = await act.Should().ThrowAsync<ArgumentException>();
		ex.Which.Message.Should().Contain("body is required");
	}

	[Fact]
	public async Task Upsert_StaleVersion_Conflicts_NothingWritten()
	{
		var http = Http();
		var node = NewNode();
		var id = (await Upsert(http, Create(node, "alice", "v1"))).Added.Single().Id;
		await Upsert(http, new CommentItemInput { Id = id, Body = "v2", Version = 1 }); // advances the comment

		// A baseline of 0 is now stale (the comment moved past it) → a conflict, nothing written.
		var stale = await Upsert(http, new CommentItemInput { Id = id, Body = "clobber", Version = 0 });
		stale.Applied.Should().BeFalse();
		stale.Updated.Should().BeEmpty();
		stale.Conflicts.Should().ContainSingle(c => c.Id == id && c.Kind == "Stale");

		(await CommentTools.GetAsync(http, Flags(), _comments, _tasks, Proj, id, bodyLen: -1)).Body.Should().Be("v2");
	}

	// card comments-upsert-conflict-drops-changedfields: the engine (TemporalStore.Classify, via
	// CommentRow.ChangedPayloadFields) always computed this; the DTO used to drop it on the floor
	// before it reached the caller (unlike tasks_upsert/memory_upsert's UpsertConflictView/
	// MemoryConflictView, which already carried it). A baseline of 0 never exercises this path
	// (the row that would be diffed against does not exist at version 0 — see
	// Upsert_StaleVersion_Conflicts_NothingWritten above), so this needs a genuine mid-air race on
	// a real read cursor: author reads at v1, someone else edits body to v2, author's own stale
	// resubmit at v1 must come back naming "body" in Conflicts[0].ChangedFields.
	[Fact]
	public async Task Upsert_GenuineRace_Conflict_CarriesChangedFields()
	{
		var http = Http();
		var node = NewNode();
		var created = await Upsert(http, Create(node, "alice", "v1"));
		var id = created.Added.Single().Id;
		var readCursor = created.Added.Single().Version;

		// Someone else moves the comment's body past the author's read.
		var other = await Upsert(http, new CommentItemInput { Id = id, Body = "v2", Version = readCursor });
		other.Applied.Should().BeTrue();

		// The author's own resubmit on the old watermark is a genuine race, not a blind retry.
		var race = await Upsert(http, new CommentItemInput { Id = id, Body = "clobber", Version = readCursor });
		race.Applied.Should().BeFalse();
		var c = race.Conflicts.Should().ContainSingle().Subject;
		c.Kind.Should().Be("Stale");
		c.ChangedFields.Should().BeEquivalentTo(["body"]);
	}

	[Fact]
	public async Task Search_List_WithoutQuery_IsChronological()
	{
		var http = Http();
		var node = NewNode();
		await Upsert(http, Create(node, "a", "alpha comment"));
		await Upsert(http, Create(node, "b", "bravo comment"));

		var res = await CommentTools.SearchAsync(http, Flags(), _comments, _tasks, Proj, board: Board, node: node);
		res.Retrievers.Should().BeNull(); // a listing carries no retrieval provenance
		res.Items.Select(c => c.Body).Should().Equal("alpha comment", "bravo comment"); // chronological
	}

	[Fact]
	public async Task Search_List_WithoutQuery_WithoutBodyLen_DefaultsToSnippet_NotFull()
	{
		// card comments-search-full-body-in-listing: the listing default used to be the FULL
		// body; it is now the same ~240-char snippet as tasks_search/memory_search (same
		// ModuleMcp.DefaultSnippet constant) — bodyLen:-1 remains the way to get it all back.
		var http = Http();
		var node = NewNode();
		var longBody = new string('x', ModuleMcp.DefaultSnippet + 100);
		await Upsert(http, Create(node, "a", longBody));

		var res = await CommentTools.SearchAsync(http, Flags(), _comments, _tasks, Proj, board: Board, node: node);

		res.Items.Should().ContainSingle();
		res.Items.Single().Body.Should().HaveLength(ModuleMcp.DefaultSnippet + 1).And.EndWith("…"); // cut + ellipsis
	}

	[Fact]
	public async Task Search_WithQuery_IsLexical_AndDegradesWithoutSemantic()
	{
		var http = Http();
		var node = NewNode();
		await Upsert(http, Create(node, "a", "the vector index cursor rebuild"));
		await Upsert(http, Create(node, "b", "an unrelated grocery list"));

		var res = await CommentTools.SearchAsync(http, Flags(), _comments, _tasks, Proj, q: "vector cursor");
		res.Items.Should().ContainSingle();
		res.Items.Single().Body.Should().Contain("vector index cursor");
		// Documented degrade: comments have no semantic leg yet → lexical floor only.
		res.Retrievers.Should().NotBeNull();
		res.Retrievers!.Lexical.Should().BeTrue();
		res.Retrievers.Semantic.Should().BeFalse();
		res.Retrievers.Degraded.Should().BeFalse();
	}

	[Fact]
	public async Task Delta_ReturnsChangesSinceCursor()
	{
		var http = Http();
		var node = NewNode();
		var first = await Upsert(http, Create(node, "a", "first"));
		var cursor = first.CurrentVersion;

		await Upsert(http, Create(node, "b", "second"));
		await Upsert(http, Create(node, "c", "third"));

		var delta = await CommentTools.DeltaAsync(http, Flags(), _comments, Proj, Board, cursor, bodyLen: -1);
		delta.Added.Select(c => c.Body).Should().BeEquivalentTo(["second", "third"]); // only post-cursor
		delta.CurrentVersion.Should().BeGreaterThan(cursor);
	}

	// card write-verbs-retry-safety-gap: a comments_upsert CREATE mints its own id server-side
	// (like memory_remember, unlike a PATCH's caller-supplied id+version), so a lost-response
	// retry had no key of its own to avoid a duplicate comment. `idempotencyKey` closes the gap
	// the same way as memory_remember: the id is DERIVED from (board, node, idempotencyKey), so
	// a retry lands on the same row and rides the ordinary temporal classifier.
	[Fact]
	public async Task Upsert_CreateWithIdempotencyKey_LostResponseRetry_DoesNotDuplicate()
	{
		var http = Http();
		var node = NewNode();
		var idem = "retry-" + Guid.NewGuid().ToString("N");
		var item = new CommentItemInput { Node = node, Author = "alice", Body = "a real comment", IdempotencyKey = idem };

		var first = await Upsert(http, item);
		first.Applied.Should().BeTrue();
		first.Added.Should().ContainSingle();
		var id = first.Added[0].Id;

		// Retry: the exact same call, verbatim — the client resending because it believes the
		// first attempt failed. Must NOT create a second comment.
		var retry = await Upsert(http, item);
		retry.Applied.Should().BeTrue();
		retry.Added.Should().BeEmpty();
		retry.Updated.Should().ContainSingle(c => c.Id == id);

		var thread = await _comments.ListForNodeAsync(Proj, Board, node);
		thread.Should().ContainSingle();
	}

	[Fact]
	public async Task Upsert_CreateWithIdempotencyKey_ReusedWithDifferentContent_IsRefused()
	{
		var http = Http();
		var node = NewNode();
		var idem = "retry-" + Guid.NewGuid().ToString("N");

		var first = await Upsert(http,
			new CommentItemInput { Node = node, Author = "alice", Body = "the first comment", IdempotencyKey = idem });
		first.Applied.Should().BeTrue();

		// A genuinely DIFFERENT comment reusing the SAME idempotencyKey must be refused, not
		// silently merged or overwritten — the same posture memory_remember takes.
		var second = await Upsert(http,
			new CommentItemInput { Node = node, Author = "alice", Body = "a totally different comment", IdempotencyKey = idem });
		second.Applied.Should().BeFalse();
		second.Conflicts.Should().ContainSingle();
		second.Conflicts[0].Kind.Should().Be("Stale");

		var thread = await _comments.ListForNodeAsync(Proj, Board, node);
		thread.Should().ContainSingle();
		thread[0].Body.Should().Be("the first comment");
	}

	[Fact]
	public async Task Upsert_CreateWithoutIdempotencyKey_EachCallStillCreatesANewComment()
	{
		// Regression control: omitting idempotencyKey must reproduce the OLD unconditional-create
		// behavior exactly — two calls with byte-identical content are two distinct comments.
		var http = Http();
		var node = NewNode();
		await Upsert(http, Create(node, "alice", "repeated verbatim on purpose"));
		await Upsert(http, Create(node, "alice", "repeated verbatim on purpose"));

		var thread = await _comments.ListForNodeAsync(Proj, Board, node);
		thread.Should().HaveCount(2);
	}

	[Fact]
	public async Task Get_MissingId_IsError()
	{
		var http = Http();
		var act = () => CommentTools.GetAsync(http, Flags(), _comments, _tasks, Proj, "no-such-comment");
		await act.Should().ThrowAsync<InvalidOperationException>();
	}
}
