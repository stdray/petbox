using System.Net;
using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Pages.ProjectHome;
using PetBox.Web.Rendering;

namespace PetBox.Tests.Web;

// The PRIVATE half of `[[#comment]]` references (work `comment-slug-and-refs`): the node detail page
// publishes a resolution map covering its WHOLE thread, so every reference a body can legitimately
// make resolves. Driven at the page-model level, which is where the map is decided — the rendering
// half is MarkdownRendererCommentRefTests, and the public half is CommentRefShareTests below.
public sealed class CommentRefPrivatePageTests : IDisposable
{
	const string Proj = "proj";
	const string Board = "work";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly CommentService _comments;
	readonly TasksService _tasks;
	readonly IMarkdownRenderer _markdown = new MarkdownRenderer();

	public CommentRefPrivatePageTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-commentref-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
		_store = new TaskBoardStore(_db.Factory(), _factory);
		_comments = new CommentService(_factory);
		_tasks = new TasksService(_store, new RelationStore(_factory), new TagStore(_factory), _comments);
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

	async Task<string> NodeAsync(string key, string body)
	{
		await _tasks.UpsertAsync(Proj, Board, [new NodePatch { Key = key, Title = "T", Body = body }]);
		return _store.GetContext(Proj).TaskNodes
			.Where(n => n.Board == Board && n.Key == key && n.ActiveTo == null).ToList().Single().NodeId;
	}

	async Task<string> CommentAsync(string nodeId, string author, string body, string? slug = null)
	{
		var r = await _comments.UpsertAsync(Proj, Board,
			[new CommentItem(null, nodeId, null, author, body, null, 0, Slug: slug)]);
		r.Applied.Should().BeTrue();
		return r.Added.Single().Id;
	}

	async Task<TaskBoardNodeModel> LoadAsync(string nodeId)
	{
		var page = new TaskBoardNodeModel(Flags(), _tasks, _comments, new NullSettingsResolver())
		{
			WorkspaceKey = "ws",
			ProjectKey = Proj,
			NodeId = nodeId,
		};
		page.PageContext = new PageContext
		{
			HttpContext = new DefaultHttpContext
			{
				User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(PetBoxClaims.IsSysAdmin, "true")], "Test")),
			},
		};
		(await page.OnGetAsync(default)).Should().BeOfType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>();
		return page;
	}

	// What the page would render for a body, using the map the page actually published. Going
	// through the real renderer (rather than asserting on the dictionary) is the point: it is the
	// pair — map plus renderer — that either produces a link or leaves text.
	string Render(TaskBoardNodeModel page, string body) =>
		_markdown.RenderToHtml(body, null, null, null, page.CommentRefs);

	[Fact]
	public async Task ThePrivatePage_ResolvesEveryCommentOfTheNode_ByBothAddresses()
	{
		var node = await NodeAsync("the-article", "table of contents: [[#part-one]]");
		var one = await CommentAsync(node, "alice", "first segment", "part-one");
		var two = await CommentAsync(node, "bob", "second segment");

		var page = await LoadAsync(node);

		Render(page, "see [[#part-one]]").Should().Contain($"href=\"#comment-{one}\"");
		Render(page, $"see [[#{two}]]").Should().Contain($"href=\"#comment-{two}\"",
			"a comment with no slug is still addressable by its id — which is what the `ref` button copies");
	}

	[Fact]
	public async Task ACommentOfANOTHERNode_DoesNotResolve_V1IsSameNodeOnly()
	{
		var here = await NodeAsync("this-article", "body");
		var elsewhere = await NodeAsync("other-article", "body");
		var mine = await CommentAsync(here, "alice", "mine", "intro");
		var theirs = await CommentAsync(elsewhere, "bob", "theirs", "intro");

		var page = await LoadAsync(here);

		Render(page, "see [[#intro]]").Should().Contain($"href=\"#comment-{mine}\"");
		page.CommentRefs.Should().NotContainKey(theirs,
			"v1 is bounded to the owning node — a cross-node map has no natural bound and would have to "
			+ "answer the confinement question all over again");
	}

	// The card's negative: a reference to a comment that is GONE degrades to text, not to a link
	// pointing at an anchor that no longer exists on the page.
	[Fact]
	public async Task ReferenceToADeletedComment_DegradesToText_NotABrokenLink()
	{
		var node = await NodeAsync("the-article", "body");
		var doomed = await CommentAsync(node, "alice", "to be deleted", "appendix");

		var before = await LoadAsync(node);
		Render(before, "see [[#appendix]]").Should().Contain($"href=\"#comment-{doomed}\"", "the control");

		(await _comments.DeleteAsync(Proj, Board, doomed)).Should().BeTrue();
		var after = await LoadAsync(node);

		var html = Render(after, "see [[#appendix]] and [[#" + doomed + "]]");
		html.Should().NotContain("<a", "the map is built from the comments the page renders, and a deleted "
			+ "comment is not one of them — no branch was needed for this");
		html.Should().Contain("[[#appendix]]");
	}

	[Fact]
	public async Task ANodeWithNoComments_PublishesAnEmptyMap_AndEveryReferenceIsText()
	{
		var node = await NodeAsync("empty-thread", "see [[#part-one]]");

		var page = await LoadAsync(node);

		page.CommentRefs.Should().BeEmpty();
		Render(page, "see [[#part-one]]").Should().NotContain("<a");
	}
}

// The PUBLIC half, over real anonymous HTTP against /ui/share/node/{token} — the four rows of the
// card's table plus the negatives, because this is the surface where getting it wrong LEAKS.
//
// The fixture is built as a trap, like NodeSharePublicPageFixture: the node body's table of contents
// references all three comments, and comment ONE's body references its neighbour TWO. So every scope
// renders text that WANTS to link outside its own grant, and a page that resolved references from
// the node's comments (instead of from the ones it is rendering) passes nothing here.
public sealed class CommentRefShareFixture : IAsyncLifetime
{
	public const string Ws = "$system";
	public const string Proj = "commentrefshareproj";
	public const string Board = "work";
	public const string Slug = "the-article";

	// Distinctive markers — every negative assertion greps for one of these, so none of them may be
	// a word that could appear in page chrome.
	public const string NodeBodyMarker = "ZZ-NODE-BODY-ZZ";
	public const string OneAuthor = "ZZ-ALICE-ZZ";
	public const string TwoAuthor = "ZZ-BOB-ZZ";
	public const string ThreeAuthor = "ZZ-CAROL-ZZ";
	public const string OneMarker = "ZZ-SEGMENT-ONE-ZZ";
	public const string TwoMarker = "ZZ-SEGMENT-TWO-ZZ";
	public const string ThreeMarker = "ZZ-SEGMENT-THREE-ZZ";

	public WebApplicationFactory<Program> Factory { get; private set; } = null!;

	public string NodeId { get; private set; } = "";
	public string OneId { get; private set; } = "";
	public string TwoId { get; private set; } = "";
	public string ThreeId { get; private set; } = "";

	public async ValueTask InitializeAsync()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Host:BackgroundServices"] = "false",
						["Features:Tasks"] = "true",
					});
				});
			});

		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using var scope = Factory.Services.CreateScope();
		using (var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open())
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Comment refs" });

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		var comments = scope.ServiceProvider.GetRequiredService<ICommentService>();

		await tasks.UpsertAsync(Proj, Board, [new NodePatch { Key = Slug, Title = "The article", Body = "placeholder" }]);
		NodeId = (await tasks.GetNodeBySlugAsync(Proj, Board, Slug))!.Node.NodeId;

		OneId = await AddAsync(comments, OneAuthor, $"{OneMarker} — and see [[#part-one]] and [[#part-two]]", "part-one");
		TwoId = await AddAsync(comments, TwoAuthor, TwoMarker, "part-two");
		ThreeId = await AddAsync(comments, ThreeAuthor, ThreeMarker, null);

		// The node body's table of contents: one reference per comment, in both address forms. Written
		// as a second pass because the id form needs a comment that does not exist yet at create time —
		// under the node's current version watermark, or the edit is refused as stale and the fixture
		// would silently test a body that has no references in it at all.
		var current = (await tasks.GetNodeBySlugAsync(Proj, Board, Slug))!.Node.Version;
		var edit = await tasks.UpsertAsync(Proj, Board,
		[
			new NodePatch
			{
				Key = Slug,
				Version = current,
				Body = $"{NodeBodyMarker}\n\ncontents: [[#part-one]], [[#part-two]], [[#{ThreeId}]]",
			},
		]);
		edit.Result.Applied.Should().BeTrue();
	}

	async Task<string> AddAsync(ICommentService comments, string author, string body, string? slug)
	{
		var r = await comments.UpsertAsync(Proj, Board,
			[new CommentItem(null, NodeId, null, author, body, null, 0, Slug: slug)]);
		r.Applied.Should().BeTrue();
		return r.Added.Single().Id;
	}

	public async ValueTask DisposeAsync() => await Factory.DisposeAsync();

	public async Task<string> MintAsync(string scope, string? commentId = null)
	{
		var token = $"tok{Guid.NewGuid():N}";
		using var s = Factory.Services.CreateScope();
		await s.ServiceProvider.GetRequiredService<INodeShareDirectory>().CreateAsync(new NodeShare
		{
			Id = token,
			ProjectKey = Proj,
			Board = Board,
			NodeId = NodeId,
			CommentId = commentId,
			Scope = scope,
			CreatedAt = DateTime.UtcNow,
			CreatedBy = "test",
		});
		return token;
	}

	public HttpClient NewAnonymousClient() =>
		Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
}

public sealed class CommentRefShareTests : IClassFixture<CommentRefShareFixture>
{
	readonly CommentRefShareFixture _fx;

	public CommentRefShareTests(CommentRefShareFixture fx) => _fx = fx;

	async Task<string> GetAsync(string token)
	{
		using var client = _fx.NewAnonymousClient();
		using var resp = await client.GetAsync($"/ui/share/node/{token}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		return await resp.Content.ReadAsStringAsync();
	}

	// ── the card's table, one test per row ───────────────────────────────────────────────────────

	[Fact]
	public async Task ScopeFull_ReferencesResolve_InsideTheShare()
	{
		var html = await GetAsync(await _fx.MintAsync(NodeShareScopes.Full));

		html.Should().Contain(CommentRefShareFixture.NodeBodyMarker);
		html.Should().Contain($"href=\"#comment-{_fx.OneId}\"");
		html.Should().Contain($"href=\"#comment-{_fx.TwoId}\"");
		html.Should().Contain($"href=\"#comment-{_fx.ThreeId}\"",
			"a full-thread share renders every comment, so every reference has a target ON THIS PAGE");
		html.Should().NotContain("[[#part-one]]", "…and therefore none of them stayed literal");
	}

	[Fact]
	public async Task ScopeBody_PublishesNoComments_SoEveryReferenceIsPlainText()
	{
		var html = await GetAsync(await _fx.MintAsync(NodeShareScopes.Body));

		html.Should().Contain(CommentRefShareFixture.NodeBodyMarker);
		html.Should().Contain("[[#part-one]]", "an unresolved reference renders as its own text");
		html.Should().NotContain("href=\"#comment-", "there is no thread on this page to link into");
		html.Should().NotContain(CommentRefShareFixture.OneAuthor)
			.And.NotContain(CommentRefShareFixture.TwoAuthor)
			.And.NotContain(CommentRefShareFixture.ThreeAuthor,
				"a reference must not disclose its target's author either — the anchor TEXT is data from "
				+ "the map, and this page publishes an empty one");
	}

	[Fact]
	public async Task ScopeComment_SelfReferenceResolves_NeighbourStaysText_AndLeaksNothingAboutIt()
	{
		var html = await GetAsync(await _fx.MintAsync(NodeShareScopes.Comment, _fx.OneId));

		html.Should().Contain(CommentRefShareFixture.OneMarker, "the granted comment is published");
		html.Should().Contain($"href=\"#comment-{_fx.OneId}\"", "a reference to ITSELF has a target on this page");

		html.Should().Contain("[[#part-two]]", "the neighbour is not published, so the reference to it is text");
		html.Should().NotContain($"href=\"#comment-{_fx.TwoId}\"",
			"a link here would either lead into a UI this reader cannot open, or disclose that the "
			+ "neighbouring comment exists at all — the grant said nothing about it");
		html.Should().NotContain(CommentRefShareFixture.TwoAuthor).And.NotContain(CommentRefShareFixture.TwoMarker);
		html.Should().NotContain(CommentRefShareFixture.ThreeAuthor).And.NotContain(CommentRefShareFixture.ThreeMarker);
	}

	// ── the affordance is not on this page, and the two flags are why ───────────────────────────
	[Fact]
	public async Task ThePublicPage_OffersNoCopyReferenceButton()
	{
		var html = await GetAsync(await _fx.MintAsync(NodeShareScopes.Full));

		html.Should().NotContain("comment-bodyref-copy",
			"the `ref` button is gated on ShowAddForm AND !ReadOnly; this page passes false/true, and a "
			+ "reader of a shared link has no body of ours to paste a reference into anyway");
	}
}
