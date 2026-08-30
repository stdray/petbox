using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;

namespace PetBox.Tests.Web;

// The PUBLIC READER of a node share link (work `node-share-public-page`; specs `node-share`,
// `node-share-scope`, `node-share-confinement`), driven over real anonymous HTTP against
// /ui/share/node/{token}.
//
// Every request in this file is made by a client with NO cookie and NO X-Api-Key. That is not a
// convenience — it is the surface under test. The token is the entire authorization, so a test that
// authenticated itself would be measuring a page nobody will ever visit.
//
// The fixture is built as a TRAP, not as a happy path: the shared node sits under a parent, blocks
// another node, has DecisionPending set, mentions a private node by `[[slug]]`, and its thread has
// three comments in two branches. Each of those is something a plausible implementation leaks —
// breadcrumbs, a relation panel, an autolinked mention carrying the target's TITLE, or the
// neighbours of the one comment that was actually published. A fixture without them would let a
// leaky page pass every assertion here.
public sealed class NodeSharePublicPageFixture : IAsyncLifetime
{
	public const string Ws = "$system";
	public const string Proj = "sharepublicproj";
	public const string Board = "work";

	public const string Slug = "published-node";
	public const string ParentSlug = "the-parent-node";
	public const string BlockedSlug = "the-blocked-node";
	public const string PrivateSlug = "private-neighbour";

	// Distinctive, unlikely-to-collide strings. Every negative assertion in this file greps the
	// response for one of these, so they must not be words that could appear in page chrome.
	public const string PrivateTitle = "ZZ-PRIVATE-NEIGHBOUR-TITLE-ZZ";
	public const string ParentTitle = "ZZ-PARENT-TITLE-ZZ";
	public const string BlockedTitle = "ZZ-BLOCKED-TITLE-ZZ";
	public const string NodeBodyMarker = "ZZ-NODE-BODY-MARKER-ZZ";
	public const string RootCommentMarker = "ZZ-ROOT-COMMENT-ZZ";
	public const string ReplyMarker = "ZZ-REPLY-TO-ROOT-ZZ";
	public const string SiblingMarker = "ZZ-SIBLING-COMMENT-ZZ";
	public const string CommentAuthor = "ZZ-AUTHOR-ZZ";

	// Named because the meta-row test asserts the rendered TEXT `P7`, not just that a priority
	// element exists — see PriorityBadge_ShowsTheNumber_NotTheUnevaluatedRazorExpression.
	public const int NodePriority = 7;

	public WebApplicationFactory<Program> Factory { get; private set; } = null!;

	public string NodeId { get; private set; } = "";
	public string RootCommentId { get; private set; } = "";
	public string SiblingCommentId { get; private set; } = "";

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
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Public share page" });

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();

		await tasks.UpsertAsync(Proj, Board,
		[
			new NodePatch { Key = ParentSlug, Title = ParentTitle, Body = "parent body" },
			new NodePatch { Key = PrivateSlug, Title = PrivateTitle, Body = "private body" },
			new NodePatch { Key = BlockedSlug, Title = BlockedTitle, Body = "blocked body" },
		]);

		// The node the token publishes. The `[[private-neighbour]]` mention is the leak trap: on the
		// SIGNED-IN node page node-ref-autolink turns it into an <a> whose text is the target's
		// TITLE. Here it must stay literal.
		await tasks.UpsertAsync(Proj, Board,
		[
			new NodePatch
			{
				Key = Slug,
				Title = "Published node",
				Body = $"{NodeBodyMarker}\n\nsee [[{PrivateSlug}]] for the rest",
				PartOf = ParentSlug,
				DecisionPending = true,
				Priority = NodePriority,
				Tags = ["area:ui"],
				Status = "InProgress",
			},
		]);

		await tasks.UpsertAsync(Proj, Board,
			[new NodePatch { Key = BlockedSlug, BlockedBy = Slug }]);

		NodeId = (await tasks.GetNodeBySlugAsync(Proj, Board, Slug))!.Node.NodeId;

		var comments = scope.ServiceProvider.GetRequiredService<ICommentService>();
		RootCommentId = (await comments.AddAsync(Proj, Board, NodeId, null, CommentAuthor, RootCommentMarker, null)).Id!;
		await comments.AddAsync(Proj, Board, NodeId, RootCommentId, CommentAuthor, ReplyMarker, null);
		SiblingCommentId = (await comments.AddAsync(Proj, Board, NodeId, null, CommentAuthor, SiblingMarker, null)).Id!;
	}

	public async ValueTask DisposeAsync() => await Factory.DisposeAsync();

	// Mints straight through the directory rather than over POST /api/share/node. The mint surface
	// has its own file (NodeShareApiAuthzTests); going through it here would make every test in this
	// file depend on an authenticated request whose only purpose is setup, and would hide which of
	// the two surfaces broke when one of them did.
	public async Task<string> MintAsync(string scope, string? commentId = null, DateTime? expiresAt = null)
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
			ExpiresAt = expiresAt,
		});
		return token;
	}

	// NO cookie jar and no api key, on purpose — see the file header.
	public HttpClient NewAnonymousClient() =>
		Factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
			HandleCookies = false,
		});
}

public sealed class NodeSharePublicPageTests : IClassFixture<NodeSharePublicPageFixture>
{
	readonly NodeSharePublicPageFixture _fx;

	public NodeSharePublicPageTests(NodeSharePublicPageFixture fx) => _fx = fx;

	async Task<string> GetHtmlAsync(string token)
	{
		using var client = _fx.NewAnonymousClient();
		using var resp = await client.GetAsync($"/ui/share/node/{token}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK, "the token is the authorization and it is valid");
		return await resp.Content.ReadAsStringAsync();
	}

	async Task<HttpStatusCode> GetStatusAsync(string token)
	{
		using var client = _fx.NewAnonymousClient();
		using var resp = await client.GetAsync($"/ui/share/node/{token}");
		return resp.StatusCode;
	}

	// ── ACCESS: THE TOKEN IS THE WHOLE AUTHORIZATION ─────────────────────────────────────────────

	[Fact]
	public async Task ThePage_OpensWithNoCookieAndNoApiKey()
	{
		var token = await _fx.MintAsync(NodeShareScopes.Body);

		using var client = _fx.NewAnonymousClient();
		using var resp = await client.GetAsync($"/ui/share/node/{token}");

		resp.StatusCode.Should().Be(HttpStatusCode.OK,
			"the whole point of a public link is that its reader has no account: a 302 to /Login here "
			+ "means the page inherited the app's default cookie policy instead of declaring itself anonymous");
		resp.Headers.Location.Should().BeNull("no redirect to a sign-in page");
	}

	[Fact]
	public async Task UnknownToken_Is404()
	{
		(await GetStatusAsync("tok-was-never-minted-at-all")).Should().Be(HttpStatusCode.NotFound);
	}

	// ── LIFETIME (spec node-share-lifetime) ──────────────────────────────────────────────────────

	[Fact]
	public async Task ExpiredLink_Is404()
	{
		var token = await _fx.MintAsync(NodeShareScopes.Body, expiresAt: DateTime.UtcNow.AddMinutes(-1));

		(await GetStatusAsync(token)).Should().Be(HttpStatusCode.NotFound,
			"an expired grant is not a grant — and it must 404 rather than say 'expired', so an anonymous "
			+ "caller cannot use the difference to learn that the token was once real");
	}

	// The null case, which is the one a hand-written `ExpiresAt < UtcNow` gets wrong: NULL would
	// compare against default(DateTime) and 404 every permanent link. NodeShare.IsExpiredAt is the
	// one predicate that handles it, and this is what notices if the page stops using it.
	[Fact]
	public async Task LinkWithNoExpiry_NeverExpires_AndServes()
	{
		var token = await _fx.MintAsync(NodeShareScopes.Body, expiresAt: null);

		var html = await GetHtmlAsync(token);

		html.Should().Contain(NodeSharePublicPageFixture.NodeBodyMarker,
			"null means 'no expiry at all', not 'expired at the dawn of time'");
	}

	[Fact]
	public async Task RevokedLink_Is404_OnTheVeryNextRequest()
	{
		var token = await _fx.MintAsync(NodeShareScopes.Body);
		(await GetStatusAsync(token)).Should().Be(HttpStatusCode.OK, "the control: it served first");

		using (var scope = _fx.Factory.Services.CreateScope())
			(await scope.ServiceProvider.GetRequiredService<IShareRevocationService>()
				.RevokeAsync(token, NodeSharePublicPageFixture.Proj)).Should().BeTrue();

		(await GetStatusAsync(token)).Should().Be(HttpStatusCode.NotFound,
			"the reader holds no state of its own, so a revoke takes effect immediately — this is the "
			+ "only way a link with no TTL is ever withdrawn");
	}

	// ── THE THREE SCOPES: EXACTLY THEIR OWN, AND NOT MORE ────────────────────────────────────────

	[Fact]
	public async Task ScopeBody_ShowsTitleAndBody_AndNotOneComment()
	{
		var html = await GetHtmlAsync(await _fx.MintAsync(NodeShareScopes.Body));

		html.Should().Contain("Published node", "the title is part of what `body` publishes");
		html.Should().Contain(NodeSharePublicPageFixture.NodeBodyMarker, "and so is the body");

		html.Should().NotContain(NodeSharePublicPageFixture.RootCommentMarker);
		html.Should().NotContain(NodeSharePublicPageFixture.ReplyMarker);
		html.Should().NotContain(NodeSharePublicPageFixture.SiblingMarker,
			"`body` publishes the node, full stop — not one comment, and not the fact that there are three");
		html.Should().NotContain("data-testid=\"comment\"",
			"not even an empty thread shell: rendering the Comments heading would disclose that a "
			+ "discussion exists, which this grant said nothing about");
		html.Should().NotContain("comments-heading");
	}

	[Fact]
	public async Task ScopeFull_ShowsTitleBodyAndTheWholeThread()
	{
		var html = await GetHtmlAsync(await _fx.MintAsync(NodeShareScopes.Full));

		html.Should().Contain("Published node");
		html.Should().Contain(NodeSharePublicPageFixture.NodeBodyMarker);
		html.Should().Contain(NodeSharePublicPageFixture.RootCommentMarker);
		html.Should().Contain(NodeSharePublicPageFixture.ReplyMarker,
			"the WHOLE thread means replies too, not just the roots");
		html.Should().Contain(NodeSharePublicPageFixture.SiblingMarker);
	}

	// The scope that is easiest to implement as "full, but scrolled to one comment" — which would
	// publish the node body and every neighbouring remark under a grant that named exactly one.
	[Fact]
	public async Task ScopeComment_ShowsOnlyThatComment_WithoutTheNodeBodyOrAnyNeighbour()
	{
		var html = await GetHtmlAsync(
			await _fx.MintAsync(NodeShareScopes.Comment, commentId: _fx.RootCommentId));

		html.Should().Contain(NodeSharePublicPageFixture.RootCommentMarker,
			"the one comment the token names IS what it publishes");

		html.Should().NotContain(NodeSharePublicPageFixture.NodeBodyMarker,
			"a grant on one comment does not publish the node's body");
		html.Should().NotContain(NodeSharePublicPageFixture.SiblingMarker,
			"nor a comment standing next to it");
		html.Should().NotContain(NodeSharePublicPageFixture.ReplyMarker,
			"nor a reply UNDER it — replies are other people's remarks, not part of the one named");
	}

	[Fact]
	public async Task ScopeComment_OnASibling_PublishesThatOneAndNotTheFirst()
	{
		var html = await GetHtmlAsync(
			await _fx.MintAsync(NodeShareScopes.Comment, commentId: _fx.SiblingCommentId));

		html.Should().Contain(NodeSharePublicPageFixture.SiblingMarker);
		html.Should().NotContain(NodeSharePublicPageFixture.RootCommentMarker,
			"the token selects a comment by id — a page that showed 'the first one' would pass the "
			+ "sibling test above by accident");
		html.Should().NotContain(NodeSharePublicPageFixture.ReplyMarker);
	}

	// The owner's explicit decision (work card, point 5), pinned so a later reader does not
	// "fix" it: the attributed remark is the thing being shared.
	[Fact]
	public async Task CommentAuthors_AreShownAsThemselves()
	{
		var html = await GetHtmlAsync(await _fx.MintAsync(NodeShareScopes.Full));

		html.Should().Contain(NodeSharePublicPageFixture.CommentAuthor,
			"authors render as they are — anonymising them would publish a quote nobody said. This is "
			+ "a decision, not an oversight; changing it needs the owner, not this test deleted");
	}

	// ── THE CONFINEMENT: `[[slug]]` STAYS TEXT (spec node-share-confinement) ─────────────────────
	//
	// THE assertion this whole card exists for. On the signed-in page node-ref-autolink resolves the
	// mention to a link whose ANCHOR TEXT is the target node's title — so a public page that passed
	// a NodeRefs map down would publish a private node's name (and a URL into a UI the reader cannot
	// open) to someone who was granted one node.
	[Fact]
	public async Task NodeMention_StaysLiteralText_LeakingNeitherLinkNorTitle()
	{
		var html = await GetHtmlAsync(await _fx.MintAsync(NodeShareScopes.Body));

		html.Should().NotContain(NodeSharePublicPageFixture.PrivateTitle,
			"the mentioned node's TITLE is the leak that matters most — the reader was granted one node "
			+ "and would learn the name of another");
		html.Should().NotContain($"/tasks/{NodeSharePublicPageFixture.Board}/{NodeSharePublicPageFixture.PrivateSlug}",
			"and no link into the closed UI");
		html.Should().NotContain("node-ref",
			"the renderer's resolved-mention markup must not appear at all");

		html.Should().Contain($"[[{NodeSharePublicPageFixture.PrivateSlug}]]",
			"an unresolved mention stays literal — this is the renderer's own documented miss behaviour, "
			+ "reached by withholding the map rather than by teaching IMarkdownRenderer a 'public' mode");
	}

	// ── THE PROJECTION: THE CUT FIELDS ARE ABSENT ────────────────────────────────────────────────

	[Fact]
	public async Task Breadcrumbs_AndRelations_AreAbsent()
	{
		var html = await GetHtmlAsync(await _fx.MintAsync(NodeShareScopes.Full));

		html.Should().NotContain(NodeSharePublicPageFixture.ParentTitle,
			"Ancestors are cut: a breadcrumb would name the node's parent, which the grant did not cover");
		html.Should().NotContain(NodeSharePublicPageFixture.BlockedTitle,
			"Relations are cut: the node BLOCKS that one, and the relation panel would name it");
		html.Should().NotContain(NodeSharePublicPageFixture.ParentSlug);
		html.Should().NotContain(NodeSharePublicPageFixture.BlockedSlug);
	}

	[Fact]
	public async Task DecisionPending_IsNotRendered()
	{
		// The fixture node really does carry the flag — otherwise this asserts nothing.
		using (var scope = _fx.Factory.Services.CreateScope())
		{
			var detail = await scope.ServiceProvider.GetRequiredService<ITasksService>()
				.GetNodeAsync(NodeSharePublicPageFixture.Proj, _fx.NodeId);
			detail!.Node.DecisionPending.Should().BeTrue("sanity: the trap is armed");
		}

		var html = await GetHtmlAsync(await _fx.MintAsync(NodeShareScopes.Full));

		html.Should().NotContain("decision-pending",
			"an internal workflow signal about the OWNER's queue is not part of publishing a node");
		html.Should().NotContain("decisionPending");
	}

	[Fact]
	public async Task SessionProvenance_AndObservationSignals_AreAbsent()
	{
		var html = await GetHtmlAsync(await _fx.MintAsync(NodeShareScopes.Full));

		html.Should().NotContain("originSession");
		html.Should().NotContain("origin-session",
			"which sessions touched this node is internal provenance, never part of the published view");
		html.Should().NotContain("observation-recurrence");
		html.Should().NotContain("regression-banner");
	}

	// ── THE READER MUST NOT BECOME A SHARE CONSOLE ───────────────────────────────────────────────
	//
	// The partial-level half of this is NodeShareUiTests' CommentThreadPartial_WithoutTheAddForm…;
	// this is the end-to-end half, over the real page, which is what proves the page actually PASSES
	// ShowAddForm:false rather than merely that the partial would honour it.
	[Fact]
	public async Task ThePage_OffersNoWayToComment_AndNoWayToMintFurtherLinks()
	{
		var html = await GetHtmlAsync(await _fx.MintAsync(NodeShareScopes.Full));

		html.Should().Contain("data-testid=\"comment\"",
			"sanity: the thread really is rendered, so the negatives below are about the controls");

		html.Should().NotContain("data-testid=\"comment-add-form\"");
		html.Should().NotContain("data-testid=\"comment-reply-form\"");
		html.Should().NotContain("data-testid=\"comment-edit-form\"",
			"an anonymous reader is offered no mutation of any kind");
		html.Should().NotContain("data-testid=\"comment-delete-form\"");
		html.Should().NotContain("data-testid=\"comment-reply-toggle\"");
		html.Should().NotContain("data-testid=\"comment-edit-toggle\"");
		html.Should().NotContain("data-node-share-open",
			"and above all no opener: a reader handed one link must not be able to mint more");
		html.Should().NotContain("data-testid=\"comment-share\"");

		// What the FIRST anonymous render of this page actually shipped, before
		// CommentThreadModel.ReadOnly existed. ShowAddForm:false was believed to be enough and was
		// not — it gated the root add form and the share button while every per-comment control
		// rendered unconditionally. These three are named individually because each is a distinct
		// leak and a partial fix would otherwise look green.
		html.Should().NotContain("__RequestVerificationToken",
			"an antiforgery token is issued to let a caller MUTATE; a page with nothing to mutate must "
			+ "not hand one to an anonymous stranger");
		html.Should().NotContain("<textarea",
			"the edit/reply forms carried each comment's body a SECOND time inside an editable "
			+ "textarea — display:none still ships the markup, so hiding them was never the fix");
		html.Should().NotContain($"value=\"{_fx.NodeId}\"",
			"the reply form's hidden nodeId field published the node's internal id, which nothing on a "
			+ "read-only page needs and which addresses the node in the closed UI");
	}

	// NodeUrl is left null, so the in-app permalink COPY button (which hands out a URL only a
	// signed-in reader can open) drops out — while the native `#comment-{id}` anchors, which cost
	// nothing and disclose nothing, stay.
	[Fact]
	public async Task NoInAppPermalinkButton_ButTheNativeAnchorsSurvive()
	{
		var html = await GetHtmlAsync(await _fx.MintAsync(NodeShareScopes.Full));

		html.Should().NotContain("data-testid=\"comment-permalink-copy\"",
			"that button copies a URL into the closed UI — useless to this reader and a pointer inward");
		html.Should().Contain($"id=\"comment-{_fx.RootCommentId}\"",
			"the native per-comment anchor is free and leaks nothing");
	}

	// ── THE META ROW SHIPS VALUES, NOT RAZOR SOURCE ──────────────────────────────────────────────
	//
	// `P@node.Priority` is not an expression in a Razor TEMPLATE: `word@word.word` trips Razor's
	// e-mail-address heuristic, so the whole run is emitted verbatim and the page shipped the
	// literal text "P@node.Priority" to every reader. Explicit parens — `P@(node.Priority)` — are
	// the fix, and the only thing that distinguishes the two is the TEXT.
	//
	// That is why this test exists at all: the page's markup was already covered, but by
	// element-presence assertions (`data-testid="share-node-priority"` is there), and a
	// presence assertion cannot tell a rendered value from the un-evaluated source that produced
	// the very same element. Anything that asserts on this row asserts on its text.
	[Fact]
	public async Task PriorityBadge_ShowsTheNumber_NotTheUnevaluatedRazorExpression()
	{
		var html = await GetHtmlAsync(await _fx.MintAsync(NodeShareScopes.Body));

		html.Should().Contain(
			$"data-testid=\"share-node-priority\">P{NodeSharePublicPageFixture.NodePriority}<",
			"the badge must carry the node's actual priority, and the element existing is not evidence "
			+ "that it does");

		html.Should().NotContain("@node",
			"no `@node…` may reach the browser: every occurrence is a Razor expression the e-mail "
			+ "heuristic swallowed and printed as source, whichever property it was reading");
	}

	// The public face of a node stretches like the PRIVATE one (ProjectHome/TaskBoardNode.cshtml,
	// which has no width wrapper at all) — owner decision, 2026-08-30. The `max-w-3xl mx-auto`
	// container this page was born with is gone; the public LOG share's `max-w-6xl` is a separate,
	// deliberate choice and not a precedent to re-copy here.
	[Fact]
	public async Task ThePage_CapsNoWidth_LikeThePrivateNodePage()
	{
		var html = await GetHtmlAsync(await _fx.MintAsync(NodeShareScopes.Full));

		html.Should().NotContain("max-w-3xl",
			"a width cap here makes the shared node narrower than the same node's private page");

		// Structural, and deliberately not a class blacklist: what was removed is the WRAPPER, and
		// the page content must now start at the body like it does on the private page. A blacklist
		// would also fire on a `max-w-*` that legitimately belongs to rendered markdown content.
		var bodyAt = html.IndexOf("<body", StringComparison.Ordinal);
		var titleAt = html.IndexOf("data-testid=\"share-node-title\"", StringComparison.Ordinal);
		html[bodyAt..titleAt].Should().NotContain("<div",
			"nothing wraps the page content any more — a container reintroduced here is exactly how "
			+ "the width cap would come back");
	}
}
