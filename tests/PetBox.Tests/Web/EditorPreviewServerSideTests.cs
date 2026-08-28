using System.Net;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;
using PetBox.Web.Pages.ProjectHome;

namespace PetBox.Tests.Web;

// editor-preview-renders-server-side: EMPIRICAL proof (real HTTP round-trips through the actual
// Razor renderer and the actual preview handler — same posture as BoardDecisionPendingUiTests)
// that the editor's live preview and the saved body are ONE pipeline, not two.
//
// The defect these close: the preview used to run ts/markdown.ts (marked + DOMPurify) in the
// browser while the saved body ran Markdig + Ganss.Xss on the server. Measured on the very body
// this fixture uses, the two produced:
//
//   client (old):  <h2>Section</h2>
//                  <blockquote>
//                  <p>[!NOTE]<br>Careful here.</p>
//                  </blockquote>
//
//   server:        <section class="md-section">
//                  <h2>Section</h2>
//                  <div class="markdown-alert markdown-alert-note">
//                  <p class="markdown-alert-title">Note</p>
//                  <p>Careful here.</p>
//                  </div>
//                  </section>
//
// — no section container, and the alert degraded to an ordinary blockquote holding the literal
// text "[!NOTE]". So the acceptance is byte identity, asserted against the body the page ACTUALLY
// serves after saving rather than against a second call to the renderer: re-invoking
// RenderToHtml on both sides would compare the fix to itself and pass no matter what the page
// emits.
public sealed class EditorPreviewFixture : IAsyncLifetime
{
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Ws = "epv-ws";
	public const string Proj = "epv-proj";
	public const string Board = "epv-board";

	// The acceptance body from the card: a `##` heading (server wraps it in `section.md-section`,
	// the old client pipeline did not) and a `> [!NOTE]` alert (server emits
	// `div.markdown-alert`, the old client pipeline emitted a blockquote with a literal "[!NOTE]").
	public const string AcceptanceBody = "## Section\n\n> [!NOTE]\n> Careful here.\n";

	public WebApplicationFactory<Program> Factory { get; private set; } = null!;

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
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Editor preview fixture" });

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		if (!await tasks.BoardExistsAsync(Proj, Board))
			await tasks.CreateBoardAsync(Proj, Board, "simple", "editor-preview-renders-server-side fixture", null, null);

		var existing = await tasks.GetAsync(Proj, Board, includeClosed: true);
		if (existing.Nodes.Count == 0)
		{
			await tasks.UpsertAsync(Proj, Board,
			[
				// One node per test that SAVES, so no test inherits another's body.
				new NodePatch { Key = "epv-identity", Title = "Byte identity", Body = "seed" },
				new NodePatch { Key = "epv-hostile", Title = "Hostile markup", Body = "seed" },
				new NodePatch { Key = "epv-refs", Title = "Link resolution", Body = "seed" },
				new NodePatch { Key = "epv-control", Title = "Control", Body = "seed" },
				// A resolvable `[[slug]]` target — mentioned by the refs test, never edited.
				new NodePatch { Key = "epv-mention-target", Title = "Mention target", Body = "I am linked to." },
			]);
		}
	}

	public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}

public sealed class EditorPreviewServerSideTests : IClassFixture<EditorPreviewFixture>
{
	const string TestPassword = "test123";
	readonly EditorPreviewFixture _fx;

	public EditorPreviewServerSideTests(EditorPreviewFixture fx) => _fx = fx;

	static string NodeUrl(string slug) =>
		$"/ui/{EditorPreviewFixture.Ws}/{EditorPreviewFixture.Proj}/tasks/{EditorPreviewFixture.Board}/{slug}";

	HttpClient NewClient() =>
		_fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

	// A minimal cookie jar. The client runs with HandleCookies=false (so each test is its own
	// isolated browser), and antiforgery here is the DOUBLE-SUBMIT pattern Program.cs describes:
	// a stable COOKIE token plus a form token randomized per render. Keeping only the auth cookie
	// — as a naive copy of the sibling suites' login helper does — makes every authenticated POST
	// a 400 no matter how correct the form token is, so the jar has to carry both.
	sealed class Jar
	{
		readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

		public void Capture(HttpResponseMessage resp)
		{
			if (!resp.Headers.TryGetValues("Set-Cookie", out var values)) return;
			foreach (var pair in values.Select(v => v.Split(';')[0]))
			{
				var eq = pair.IndexOf('=', StringComparison.Ordinal);
				if (eq <= 0) continue;
				_cookies[pair[..eq]] = pair[(eq + 1)..];
			}
		}

		public string Header => string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}"));

		public async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage req)
		{
			if (Header.Length > 0) req.Headers.Add("Cookie", Header);
			var resp = await client.SendAsync(req);
			Capture(resp);
			return resp;
		}
	}

	// Log in and return (the jar, the node page's HTML) — both needed by every subsequent POST.
	static async Task<(Jar Cookies, string Html)> LoginAndGetAsync(HttpClient client, string url)
	{
		var jar = new Jar();
		var loginPage = await jar.SendAsync(client, new HttpRequestMessage(HttpMethod.Get, "/Login"));
		var token = ExtractAntiforgery(await loginPage.Content.ReadAsStringAsync());

		var loginReq = new HttpRequestMessage(HttpMethod.Post, "/Login?returnUrl=" + Uri.EscapeDataString(url))
		{
			Content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["username"] = "admin",
				["password"] = TestPassword,
				["returnUrl"] = url,
				["__RequestVerificationToken"] = token,
			}),
		};
		await jar.SendAsync(client, loginReq);

		var page = await jar.SendAsync(client, new HttpRequestMessage(HttpMethod.Get, url));
		page.StatusCode.Should().Be(HttpStatusCode.OK);
		return (jar, await page.Content.ReadAsStringAsync());
	}

	static string ExtractAntiforgery(string html)
	{
		var tokenStart = html.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		tokenStart.Should().BeGreaterThan(-1, "the page must carry an antiforgery token to post with");
		var valueStart = html.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var valueEnd = html.IndexOf('"', valueStart);
		return html[valueStart..valueEnd];
	}

	static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Jar cookies,
		Dictionary<string, string> form) =>
		await cookies.SendAsync(client,
			new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) });

	// The preview endpoint, exactly as the hidden trigger span calls it: POST ?handler=Preview on
	// the node's own URL, form-encoded `body` + the page's antiforgery token.
	static async Task<HttpResponseMessage> PreviewAsync(HttpClient client, string nodeUrl, Jar cookies,
		string pageHtml, string body) =>
		await PostAsync(client, nodeUrl + "?handler=Preview", cookies, new Dictionary<string, string>
		{
			["body"] = body,
			["__RequestVerificationToken"] = ExtractAntiforgery(pageHtml),
		});

	// Save a body through the real edit form and return the reloaded page's HTML.
	static async Task<string> SaveAndReloadAsync(HttpClient client, string nodeUrl, Jar cookies,
		string pageHtml, string body)
	{
		var version = ExtractAttr(pageHtml, "<input type=\"hidden\" name=\"version\" value=\"", "\"");
		var save = await PostAsync(client, nodeUrl + "?handler=Edit", cookies, new Dictionary<string, string>
		{
			["title"] = ExtractAttr(pageHtml, "name=\"title\" value=\"", "\""),
			["body"] = body,
			["version"] = version,
			["__RequestVerificationToken"] = ExtractAntiforgery(pageHtml),
		});
		save.StatusCode.Should().BeOneOf(HttpStatusCode.Found, HttpStatusCode.Redirect);

		var reloaded = await cookies.SendAsync(client, new HttpRequestMessage(HttpMethod.Get, nodeUrl));
		reloaded.StatusCode.Should().Be(HttpStatusCode.OK);
		return await reloaded.Content.ReadAsStringAsync();
	}

	static string ExtractAttr(string html, string prefix, string terminator)
	{
		var start = html.IndexOf(prefix, StringComparison.Ordinal);
		start.Should().BeGreaterThan(-1, $"the page must contain '{prefix}'");
		start += prefix.Length;
		var end = html.IndexOf(terminator, start, StringComparison.Ordinal);
		return html[start..end];
	}

	// The saved body as the page actually serves it: the inner HTML of the read-body div. Pulled
	// out of the live response, NOT re-rendered — that is what keeps the identity assertion honest.
	static string SavedBodyHtml(string pageHtml)
	{
		const string Marker = "data-testid=\"node-body\"";
		var at = pageHtml.IndexOf(Marker, StringComparison.Ordinal);
		at.Should().BeGreaterThan(-1, "the node page must render the saved body");
		var open = pageHtml.IndexOf('>', at) + 1;

		// Walk to the matching </div> so a body containing its own <div> (an alert is one) doesn't
		// truncate the capture at the first close tag.
		var depth = 1;
		var i = open;
		while (depth > 0)
		{
			var nextOpen = pageHtml.IndexOf("<div", i, StringComparison.Ordinal);
			var nextClose = pageHtml.IndexOf("</div>", i, StringComparison.Ordinal);
			nextClose.Should().BeGreaterThan(-1, "the read-body div must be closed");
			if (nextOpen >= 0 && nextOpen < nextClose) { depth++; i = nextOpen + 4; }
			else { depth--; if (depth == 0) return pageHtml[open..nextClose]; i = nextClose + 6; }
		}
		throw new InvalidOperationException("unbalanced markup");
	}

	// ── THE ACCEPTANCE ────────────────────────────────────────────────────────────────────────
	// The card's criterion: the same body yields byte-for-byte identical HTML in the preview and
	// after saving. Fails under the old behaviour, where the preview never reached the server at
	// all and the browser rendered `##`/`> [!NOTE]` through a different library.
	[Fact]
	public async Task PreviewHtml_IsByteIdenticalTo_SavedBodyHtml()
	{
		var client = NewClient();
		var url = NodeUrl("epv-identity");
		var (cookies, pageHtml) = await LoginAndGetAsync(client, url);

		var preview = await PreviewAsync(client, url, cookies, pageHtml, EditorPreviewFixture.AcceptanceBody);
		preview.StatusCode.Should().Be(HttpStatusCode.OK);
		var previewHtml = await preview.Content.ReadAsStringAsync();

		var savedHtml = SavedBodyHtml(await SaveAndReloadAsync(client, url, cookies, pageHtml,
			EditorPreviewFixture.AcceptanceBody));

		// CONTROL — without these, an endpoint returning "" and a page rendering "" would satisfy
		// the equality below and this test would pass while proving nothing. Both sides must carry
		// the two structures that the old client pipeline could NOT produce.
		previewHtml.Should().NotBeNullOrWhiteSpace("an empty preview must never satisfy this test");
		previewHtml.Should().Contain("<section class=\"md-section\"",
			"`##` must open a design-layer section — the old client pipeline emitted a bare <h2>");
		previewHtml.Should().Contain("markdown-alert-note",
			"`> [!NOTE]` must become an alert — the old client pipeline emitted a blockquote with a literal \"[!NOTE]\"");
		savedHtml.Should().Contain("<section class=\"md-section\"");
		savedHtml.Should().Contain("markdown-alert-note");

		previewHtml.Should().Be(savedHtml,
			"the preview and the saved body must come out of ONE markdown pipeline, byte for byte");
	}

	// CONTROL for the assertion above: prove the comparison can actually FAIL. If SavedBodyHtml or
	// the endpoint were stubbed to a constant, this would pass too — so pin that a DIFFERENT body
	// renders DIFFERENTLY, i.e. the equality test above is sensitive to its input.
	[Fact]
	public async Task PreviewHtml_DiffersWhenTheBodyDiffers()
	{
		var client = NewClient();
		var url = NodeUrl("epv-control");
		var (cookies, pageHtml) = await LoginAndGetAsync(client, url);

		var a = await (await PreviewAsync(client, url, cookies, pageHtml, EditorPreviewFixture.AcceptanceBody))
			.Content.ReadAsStringAsync();
		var b = await (await PreviewAsync(client, url, cookies, pageHtml, "## Section\n\nplain paragraph, no alert\n"))
			.Content.ReadAsStringAsync();

		a.Should().NotBe(b, "if two different bodies render identically the identity assertion is vacuous");
		a.Should().Contain("markdown-alert-note");
		b.Should().NotContain("markdown-alert-note");
	}

	// ── SANITIZER, IN THE NEGATIVE DIRECTION ──────────────────────────────────────────────────
	// The endpoint renders arbitrary caller-supplied markup, so prove the hostile input is
	// neutralized IN THE PREVIEW RESPONSE — not only after saving.
	[Fact]
	public async Task HostileBody_IsNeutralizedInThePreviewResponse_AndMatchesTheSavedRender()
	{
		const string Hostile =
			"""
			# Title

			<script>alert('xss')</script>

			<img src="x" onerror="alert('xss')">

			<a href="javascript:alert('xss')">click</a>

			<div onclick="alert('xss')">div</div>
			""";

		var client = NewClient();
		var url = NodeUrl("epv-hostile");
		var (cookies, pageHtml) = await LoginAndGetAsync(client, url);

		var preview = await PreviewAsync(client, url, cookies, pageHtml, Hostile);
		preview.StatusCode.Should().Be(HttpStatusCode.OK);
		var previewHtml = await preview.Content.ReadAsStringAsync();

		previewHtml.Should().NotContain("<script", "the preview must not ship an executable script tag");
		previewHtml.Should().NotContain("onerror", "event handlers must be stripped in the preview");
		previewHtml.Should().NotContain("onclick", "event handlers must be stripped in the preview");
		previewHtml.Should().NotContain("javascript:", "javascript: URLs must be stripped in the preview");

		// CONTROL: the sanitizer must be REMOVING things from a real render, not receiving nothing.
		previewHtml.Should().Contain("Title", "the benign content must survive — proving the render ran at all");

		// And the neutralization is the same neutralization, not a second, weaker one.
		var savedHtml = SavedBodyHtml(await SaveAndReloadAsync(client, url, cookies, pageHtml, Hostile));
		previewHtml.Should().Be(savedHtml, "one sanitizer, one allow-list, one result");
	}

	// ── LINK RESOLUTION ───────────────────────────────────────────────────────────────────────
	// `[[slug]]` mentions and commit hashes are resolved by a server pre-scan before rendering. The
	// card is explicit that if they did NOT resolve in the preview, the fix would have swapped the
	// old divergence for a new one — so pin that they resolve, and that identity still holds WITH
	// them resolved. (The saved-body path pre-scans the body plus every comment body, so its map
	// can hold entries the preview's does not; that cannot move the output, because only mentions
	// actually present in the rendered text are ever looked up — this test is what proves it.)
	[Fact]
	public async Task NodeMentions_ResolveIdenticallyInPreviewAndSavedBody()
	{
		const string Body = "see [[epv-mention-target]] and [[epv-no-such-node]] plus 0123456789abcdef01234567\n";

		var client = NewClient();
		var url = NodeUrl("epv-refs");
		var (cookies, pageHtml) = await LoginAndGetAsync(client, url);

		var previewHtml = await (await PreviewAsync(client, url, cookies, pageHtml, Body)).Content.ReadAsStringAsync();
		var savedHtml = SavedBodyHtml(await SaveAndReloadAsync(client, url, cookies, pageHtml, Body));

		// The resolvable mention became a link on BOTH sides…
		previewHtml.Should().Contain("epv-mention-target</a>",
			"a `[[slug]]` that resolves must be a link in the preview, not literal text");
		savedHtml.Should().Contain("epv-mention-target</a>");
		// …and the unresolvable one stayed literal on both.
		previewHtml.Should().Contain("[[epv-no-such-node]]");
		savedHtml.Should().Contain("[[epv-no-such-node]]");

		// Byte identity is the actual requirement, and it holds WITH resolution in play.
		previewHtml.Should().Be(savedHtml,
			"link resolution must run over the DRAFT text with the same context the saved body uses");
	}

	// ── THE ENDPOINT'S OWN FENCES ─────────────────────────────────────────────────────────────
	[Fact]
	public async Task Preview_WithoutAuthentication_DoesNotRender()
	{
		var client = NewClient();
		var resp = await client.PostAsync(NodeUrl("epv-identity") + "?handler=Preview",
			new FormUrlEncodedContent(new Dictionary<string, string> { ["body"] = "## secret" }));

		resp.StatusCode.Should().NotBe(HttpStatusCode.OK,
			"an unauthenticated caller must never reach the renderer");
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().NotContain("md-section", "no rendered markdown may leak to an anonymous caller");
	}

	[Fact]
	public async Task Preview_WithoutAntiforgeryToken_IsRejected()
	{
		var client = NewClient();
		var url = NodeUrl("epv-identity");
		var (cookies, _) = await LoginAndGetAsync(client, url);

		var resp = await PostAsync(client, url + "?handler=Preview", cookies,
			new Dictionary<string, string> { ["body"] = "## no token" });

		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
			"the preview is a POST on a cookie-authenticated page — it must carry the antiforgery token like every other one");
	}

	[Fact]
	public async Task Preview_OverTheSizeCeiling_RefusesInsteadOfRendering()
	{
		var client = NewClient();
		var url = NodeUrl("epv-identity");
		var (cookies, pageHtml) = await LoginAndGetAsync(client, url);

		var oversized = new string('a', TaskBoardNodeModel.MaxPreviewChars + 1);
		var resp = await PreviewAsync(client, url, cookies, pageHtml, "## head\n\n" + oversized);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("too large to preview");
		html.Should().NotContain("md-section", "an over-ceiling body must not be rendered at all");
		html.Should().Contain("can still be saved", "the ceiling is on previewing, not on saving");
	}

	[Fact]
	public async Task Preview_AtTheSizeCeiling_StillRenders()
	{
		var client = NewClient();
		var url = NodeUrl("epv-identity");
		var (cookies, pageHtml) = await LoginAndGetAsync(client, url);

		// Exactly at the limit — the boundary is inclusive, so this must render normally. Without
		// this the ceiling test above would pass against a handler that refuses EVERYTHING.
		const string Head = "## head\n\n";
		var atLimit = Head + new string('a', TaskBoardNodeModel.MaxPreviewChars - Head.Length);
		atLimit.Length.Should().Be(TaskBoardNodeModel.MaxPreviewChars);

		var html = await (await PreviewAsync(client, url, cookies, pageHtml, atLimit)).Content.ReadAsStringAsync();
		html.Should().Contain("md-section");
		html.Should().NotContain("too large to preview");
	}
}
