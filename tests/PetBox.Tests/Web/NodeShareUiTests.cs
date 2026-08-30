using System.Net;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Web.Pages.Shared;

namespace PetBox.Tests.Web;

// node-share-ui: the CLIENT half of the node share link (spec `node-share`, `node-share-scope`).
// The mint endpoint itself (POST /api/share/node) is the sibling card node-share-backend's — these
// tests deliberately assert only what this card owns: that the controls exist on the surfaces they
// belong to, that they are ABSENT on the surface they must never reach, and that the shipped script
// aims at the agreed endpoints. The endpoint assertions read ts/nodeShare.ts off the repo rather
// than the built bundle, the same posture as ShareApiAuthzTests's revoke-button test: wwwroot/js is
// produced by the Release-only BuildFrontend target, so a Debug run has nothing to fetch and the
// assertion must mean the same thing in every configuration.
public sealed class NodeShareUiFixture : IAsyncLifetime
{
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Ws = "nodeshare-ws";
	public const string Proj = "nodeshare-proj";
	public const string Board = "nodeshare-board";
	public const string Slug = "shareable-node";

	public WebApplicationFactory<Program> Factory { get; private set; } = null!;
	// The comment the per-comment share button must be wired to — captured so the test can assert
	// the button carries THIS id, not merely that some share button exists in the thread.
	public string CommentId { get; private set; } = "";
	public string NodeId { get; private set; } = "";

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
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = TestPasswordHash,
					});
				});
			});

		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
			await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
		if (!await db.Projects.AnyAsync(p => p.Key == Proj))
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Node share UI fixture" });

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		if (!await tasks.BoardExistsAsync(Proj, Board))
			await tasks.CreateBoardAsync(Proj, Board, "simple", "node-share-ui fixture", null, null);

		var existing = await tasks.GetAsync(Proj, Board, includeClosed: true);
		if (existing.Nodes.Count == 0)
			await tasks.UpsertAsync(Proj, Board,
				[new NodePatch { Key = Slug, Title = "Shareable node", Body = "the node body" }]);

		NodeId = (await tasks.GetAsync(Proj, Board, includeClosed: true))
			.Nodes.Single(n => n.Key == Slug).NodeId;

		var comments = scope.ServiceProvider.GetRequiredService<ICommentService>();
		var already = await comments.ListForNodeAsync(Proj, Board, NodeId);
		CommentId = already.Count > 0
			? already[0].Id
			: (await comments.AddAsync(Proj, Board, NodeId, null, "tester", "a remark worth sharing", null)).Id ?? "";
	}

	public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}

public sealed class NodeShareUiTests : IClassFixture<NodeShareUiFixture>
{
	const string TestPassword = "test123";
	readonly NodeShareUiFixture _fx;

	public NodeShareUiTests(NodeShareUiFixture fx) => _fx = fx;

	HttpClient NewClient() =>
		_fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

	// Same cookie-login walk BoardDecisionPendingUiTests uses (own cookie jar, antiforgery scraped
	// off /Login), so each test drives a REAL HTTP round trip through the actual Razor renderer.
	static async Task<HttpResponseMessage> GetAuthedAsync(HttpClient client, string url)
	{
		var resp = await client.GetAsync(url);
		if (resp.StatusCode != HttpStatusCode.Found)
			return resp;

		var loginPage = await client.GetAsync("/Login");
		var loginHtml = await loginPage.Content.ReadAsStringAsync();
		var tokenStart = loginHtml.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = loginHtml.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var valueEnd = loginHtml.IndexOf('"', valueStart);
		var token = loginHtml[valueStart..valueEnd];
		var cookies = loginPage.Headers.GetValues("Set-Cookie").ToList();

		var loginReq = new HttpRequestMessage(HttpMethod.Post, "/Login?returnUrl=" + Uri.EscapeDataString(url));
		loginReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = "admin",
			["password"] = TestPassword,
			["returnUrl"] = url,
			["__RequestVerificationToken"] = token,
		});
		foreach (var c in cookies) loginReq.Headers.Add("Cookie", c.Split(';')[0]);

		var loginResp = await client.SendAsync(loginReq);
		var authCookie = loginResp.Headers.GetValues("Set-Cookie").First().Split(';')[0];
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", authCookie);
		return await client.SendAsync(req);
	}

	async Task<string> NodePageHtmlAsync()
	{
		var url = $"/ui/{NodeShareUiFixture.Ws}/{NodeShareUiFixture.Proj}/tasks/node/{_fx.NodeId}";
		var resp = await GetAuthedAsync(NewClient(), url);
		resp.StatusCode.Should().Be(HttpStatusCode.OK, "the node detail page must load for the admin");
		return await resp.Content.ReadAsStringAsync();
	}

	// ── THE NODE BUTTON ──────────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task NodePage_OffersAShareButton_ThatOpensAModalCarryingTheNodesIdentity()
	{
		var html = await NodePageHtmlAsync();

		html.Should().Contain("data-testid=\"node-share-open\"",
			"the node page must offer a Share control at all — the whole point of the card");
		html.Should().Contain("data-testid=\"node-share-modal\"",
			"and the modal it opens must be on the page, not a page the button navigates to");

		// The mint request needs project+board+node. They live on the DIALOG (so per-comment
		// buttons don't have to repeat them), which makes their presence there load-bearing: a
		// modal missing any one of them would POST an unusable body at click time.
		html.Should().Contain($"data-project=\"{NodeShareUiFixture.Proj}\"");
		html.Should().Contain($"data-board=\"{NodeShareUiFixture.Board}\"");
		html.Should().Contain($"data-node-id=\"{_fx.NodeId}\"");
	}

	// spec node-share-scope: the choice must be offered AT MINT TIME. A modal with only one
	// implicit scope would satisfy "there is a share button" while silently deciding for the user.
	[Fact]
	public async Task NodeShareModal_OffersBothScopes_BodyOnlyAndEverythingWithTheDiscussion()
	{
		var html = await NodePageHtmlAsync();

		html.Should().Contain("data-testid=\"node-share-scope-body\"", "\"body only\" must be selectable");
		html.Should().Contain("data-testid=\"node-share-scope-full\"",
			"\"everything, including the discussion\" must be selectable — the scope is baked into the "
			+ "token at mint time, so a reader can never widen it afterwards");
		html.Should().Contain("value=\"body\"");
		html.Should().Contain("value=\"full\"");
	}

	// An indefinite link is only defensible if it can be withdrawn — see how the log modal shipped
	// without this and left DELETE /api/share/{token} reachable by curl only (share-link-revocation
	// -finish). This modal ships the control from day one.
	[Fact]
	public async Task NodeShareModal_CarriesACopyAndARevokeControl()
	{
		var html = await NodePageHtmlAsync();

		html.Should().Contain("data-testid=\"node-share-copy\"");
		html.Should().Contain("data-testid=\"node-share-revoke\"",
			"an unbounded link with no revoke control is a link that can never be taken back");
	}

	// node-share-scope-switch-orphans-live-links: issuing a public token must be something the user
	// ASKED for. The modal used to mint on open and again on every scope change, so merely looking
	// at the dialog leaked an indefinite link nothing could name afterwards. The markup half of the
	// fix is a Create control plus an inert preview; the behavioural half (open/scope-change send no
	// POST, and no minted token loses its Revoke) is asserted in ts/nodeShare.test.ts, which the
	// Cake `Test` target runs through WebTsTest.
	[Fact]
	public async Task NodeShareModal_IssuesOnlyOnAnExplicitControl_AndStartsWithEveryLinkActionInert()
	{
		var html = await NodePageHtmlAsync();

		html.Should().Contain("data-testid=\"node-share-create\"",
			"minting a public capability token must be an explicit press, not a side effect of opening a dialog");
		html.Should().Contain("data-node-share-create",
			"and the script must have an attribute hook to listen for it");

		// The pre-mint state: the URL field and both link actions are present but disabled, so the
		// modal shows what a link WILL look like without pretending one exists.
		html.Should().Contain("readonly disabled data-testid=\"node-share-url\"",
			"there is no link yet, so the URL field must not look like it holds one");
		html.Should().Contain("disabled data-node-share-copy",
			"nothing to copy before a link is issued");
		html.Should().Contain("data-node-share-revoke data-testid=\"node-share-revoke\"",
			"nothing to revoke before a link is issued either — the control is inert, not absent");

		// Each issued link gets its OWN row with its OWN Revoke: a second link is added beside the
		// first, never on top of it. That row template is what makes "a live token with no revoke
		// button" impossible while the page lives.
		html.Should().Contain("data-node-share-row-template",
			"issued links are rendered from a template, one row each");
		html.Should().Contain("data-testid=\"node-share-link-revoke\"",
			"and every issued link carries its own Revoke — the previous one is not displaced by the next");
	}

	// ── THE COMMENT BUTTON ───────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task NodePage_CommentRow_OffersShareComment_WiredToThatCommentsId()
	{
		var html = await NodePageHtmlAsync();

		html.Should().Contain("data-testid=\"comment-share\"",
			"the thread must offer \"share this comment\" next to the existing Copy link control");
		html.Should().Contain($"data-comment-id=\"{_fx.CommentId}\"",
			"and it must name the comment it shares — a scope=comment mint with no comment id is unusable");
		html.Should().Contain("data-scope=\"comment\"");

		// The pre-existing in-app permalink control must still be there: the public share link is an
		// ADDITION beside it, not a replacement for it (they hand out different things — one needs
		// an account, the other does not).
		html.Should().Contain("data-testid=\"comment-permalink-copy\"",
			"the private Copy-link control must survive — the public share button sits beside it");
	}

	// ── THE LEAK GUARD ───────────────────────────────────────────────────────────────────────────
	//
	// THE requirement that is easy to get wrong. The public share page renders this SAME partial for
	// an ANONYMOUS reader, with ShowAddForm:false. If the share button were not gated, a reader who
	// was handed one link could mint fresh public links of their own from it — the shared page would
	// become a share console. Rendering the partial directly (through the app's real Razor view
	// engine, so this is the actual compiled _CommentThread.cshtml, not a copy of it) is what lets
	// this assert the false case today, before that public page exists in this branch.
	[Fact]
	public async Task CommentThreadPartial_WithoutTheAddForm_RendersNoShareControlAtAll()
	{
		var withForm = await RenderCommentThreadAsync(showAddForm: true);
		withForm.Should().Contain("data-testid=\"comment-share\"",
			"sanity: the same partial DOES render the control on the node's own authoring surface — "
			+ "otherwise the negative assertion below would pass for the wrong reason");

		var withoutForm = await RenderCommentThreadAsync(showAddForm: false);
		withoutForm.Should().Contain("data-testid=\"comment\"",
			"sanity: the thread itself still renders — only the share affordance is supposed to drop out");
		withoutForm.Should().NotContain("data-testid=\"comment-share\"",
			"a surface that does not offer to comment must not offer to MINT PUBLIC LINKS either: this "
			+ "is exactly the public share page, where the reader is anonymous");
		withoutForm.Should().NotContain("data-node-share-open",
			"not merely the test id — no opener attribute of any kind may reach that surface");
	}

	// Renders the real _CommentThread.cshtml through the application's own view engine. The route
	// values are the node page's (that page is the partial's only in-app caller), so the
	// asp-page-handler forms inside it can generate their URLs exactly as they do in a live request.
	async Task<string> RenderCommentThreadAsync(bool showAddForm)
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var services = scope.ServiceProvider;

		var comments = services.GetRequiredService<ICommentService>();
		var lines = CommentThread.Flatten(
			await comments.ListForNodeAsync(NodeShareUiFixture.Proj, NodeShareUiFixture.Board, _fx.NodeId));
		lines.Should().NotBeEmpty("the fixture seeds a comment, so the thread under test is non-empty");

		var routeData = new RouteData();
		routeData.Values["page"] = "/ProjectHome/TaskBoardNode";
		routeData.Values["workspaceKey"] = NodeShareUiFixture.Ws;
		routeData.Values["projectKey"] = NodeShareUiFixture.Proj;
		routeData.Values["nodeId"] = _fx.NodeId;

		var httpContext = new DefaultHttpContext { RequestServices = services };
		httpContext.Request.Scheme = "https";
		httpContext.Request.Host = new HostString("example.test");
		// The partial's asp-page-handler forms generate their action URLs through IUrlHelperFactory,
		// which picks the ENDPOINT-routing url helper only when the context carries an endpoint (and
		// reads ambient route values off the request, not off RouteData). Without both, the tag
		// helper falls back to the legacy IRouter path and throws "Could not find an IRouter". The
		// endpoint is a placeholder — the LinkGenerator resolving the page comes from the real app.
		httpContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "node-share-ui-test"));
		httpContext.Request.RouteValues = new RouteValueDictionary(routeData.Values);
		var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

		var engine = services.GetRequiredService<ICompositeViewEngine>();
		var found = engine.GetView(executingFilePath: null, viewPath: "/Pages/Shared/_CommentThread.cshtml", isMainPage: false);
		found.Success.Should().BeTrue("the shared comment-thread partial must be resolvable by path");
		var view = found.View!;

		var viewData = new ViewDataDictionary<CommentThreadModel>(
			new EmptyModelMetadataProvider(), new ModelStateDictionary())
		{
			Model = new CommentThreadModel(lines, _fx.NodeId, ShowAddForm: showAddForm,
				NodeUrl: "https://example.test/ui/x/y/tasks/b/s"),
		};
		var tempData = new TempDataDictionary(httpContext, services.GetRequiredService<ITempDataProvider>());

		await using var writer = new StringWriter();
		var viewContext = new ViewContext(actionContext, view, viewData, tempData, writer, new HtmlHelperOptions());
		await view.RenderAsync(viewContext);
		return writer.ToString();
	}

	// ── THE WIRING ───────────────────────────────────────────────────────────────────────────────
	//
	// Buttons nothing listens to would satisfy every markup assertion above while changing nothing
	// for the user, so the shipped script is asserted against the agreed contract too.
	[Fact]
	public void NodeShareScript_MintsThroughTheNodeEndpoint_WithTheAgreedRequestShape()
	{
		var script = NodeShareScriptSource();

		script.Should().Contain("target.closest<HTMLElement>(\"[data-node-share-open]\")",
			"the document click handler must listen for the openers — a querySelector elsewhere is not a listener");
		script.Should().Contain("\"/api/share/node\"", "minting goes through the agreed node endpoint");
		script.Should().Contain("method: \"POST\"");
		script.Should().Contain("{ projectKey: project, board, nodeId, scope }",
			"the agreed request body: projectKey + board + nodeId + scope");
		script.Should().Contain("body[\"commentId\"] = commentId",
			"and commentId, only for the comment scope");

		script.Should().Contain("`${window.location.origin}/ui/share/node/${created.id}`",
			"the public address is built from the returned id per the contract");

		// The card's default: indefinite. Sending a ttl would quietly give every task link an expiry
		// the UI never mentioned, so its ABSENCE from the request is the assertion. Matched on the
		// two shapes that would actually SEND one (an object-literal property, or a keyed write into
		// the request body) rather than on the bare word — the module's header comment explains why
		// the field is omitted, and that prose must not be what makes this test pass or fail.
		script.Should().NotContain("ttlMinutes:",
			"ttlMinutes is deliberately not sent — the link is indefinite until revoked");
		script.Should().NotContain("[\"ttlMinutes\"]",
			"and it is not written into the request body under that key either");
		script.Should().Contain("Never expires",
			"and the modal must SAY so rather than leaving the expiry strip blank");

		// node-share-scope-switch-orphans-live-links. Two structural claims, because the behavioural
		// ones (opening and scope-switching send no POST) belong in ts/nodeShare.test.ts:
		script.Should().Contain("[data-node-share-create]",
			"minting must hang off an explicit control — it used to fire from showModal() and from the "
			+ "scope radios, which leaked an indefinite token on every glance at the dialog");
		script.Should().NotContain("let minted",
			"and no single module-level slot may hold \"the\" token: overwriting that variable is what "
			+ "made every previous token unrevocable — a share token is addressable only by value, and "
			+ "there is no verb that lists them");
	}

	[Fact]
	public void NodeShareScript_RevokesThroughTheTokenEndpoint_AfterConfirming()
	{
		var script = NodeShareScriptSource();

		script.Should().Contain("target.closest<HTMLElement>(\"[data-node-share-revoke]\")",
			"the Revoke control must be listened for");
		script.Should().Contain("method: \"DELETE\"");
		script.Should().Contain("`/api/share/${encodeURIComponent(token)}`",
			"aimed at the revoke endpoint for the token of the row whose button was pressed");
		script.Should().Contain("JSON.stringify({ projectKey: project })",
			"the revoke contract carries the project in the body");
		script.Should().Contain("window.confirm(",
			"revocation is irreversible — it asks first, like the log share modal");

		// The token's ONLY record on the client is its own row, so the row may go only after the
		// server confirmed the DELETE. Dropping it first would hide a still-live token on any failure.
		script.Should().Contain("row.dataset[\"token\"]",
			"each row carries the token its own Revoke button withdraws");
		script.Should().Contain("row.remove();",
			"and the row is what disappears on a successful revoke — nothing else was holding the token");
	}

	// The site bundle must actually pull the module in; a module nothing imports ships to nobody.
	[Fact]
	public void SiteBundle_InitialisesTheNodeShareModule()
	{
		var site = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PetBox.Web", "ts", "site.ts"));
		site.Should().Contain("initNodeShare", "the entrypoint must import and run the module");
	}

	static string NodeShareScriptSource() =>
		File.ReadAllText(Path.Combine(RepoRoot(), "src", "PetBox.Web", "ts", "nodeShare.ts"));

	// The repo-root walk ShareApiAuthzTests uses (reading SOURCE keeps these assertions
	// configuration-independent — see this file's header).
	static string RepoRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(dir, "src", "PetBox.Web")))
			dir = Path.GetDirectoryName(dir);

		dir.Should().NotBeNullOrEmpty("the repo root (with src/PetBox.Web) must be findable from the test bin");
		return dir!;
	}
}
