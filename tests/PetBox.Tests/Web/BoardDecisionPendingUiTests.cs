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

namespace PetBox.Tests.Web;

// decision-pending-has-no-ui: EMPIRICAL proof (real HTTP round-trips through the actual Razor
// renderer, not a PageModel-only check — see BoardSearchIndexEncodingTests for the same posture on
// a sibling feature) that:
//   1. the board page shows a badge on a decisionPending node without opening it,
//   2. the "waiting only" filter is a REAL narrowing — an excluded node's markup never reaches the
//      wire at all (not merely hidden by inline style/CSS), reverse direction included,
//   3. the same `?decisionPending=true` URL renders identically from a second, entirely
//      independent browser context (no shared cookies) — the shareability acceptance bullet,
//   4. the node detail page's new form actually flips the flag, and the result is what
//      tasks_node_get (ITasksService.GetNodeAsync, the same read the MCP tool wraps) reports.
public sealed class BoardDecisionPendingUiFixture : IAsyncLifetime
{
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Ws = "dpui-ws";
	public const string Proj = "dpui-proj";
	public const string Board = "dpui-board";
	public const string EmptyBoard = "dpui-empty-board";
	// A SEPARATE board for every test that issues an explicit `?decisionPending=` query: that
	// query is not read-only — TaskBoardModel.LoadAsync persists it into the SAME admin user's
	// per-(project,board) saved preference every `view`/`fields` navigation already does
	// (board-view-cross-device). Reusing `Board` for those would leave a later, unrelated
	// no-query-string request against `Board` inheriting a stale "filter is on" preference from
	// whichever filtering test happened to run first — a test-isolation hazard, not a product bug
	// (a real second visit to the SAME board, by the SAME user, is SUPPOSED to remember its own
	// last explicit choice).
	public const string FilteredBoard = "dpui-filtered-board";

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
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Decision Pending UI Fixture" });

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		if (!await tasks.BoardExistsAsync(Proj, Board))
			await tasks.CreateBoardAsync(Proj, Board, "simple", "decision-pending-has-no-ui fixture", null, null);
		if (!await tasks.BoardExistsAsync(Proj, EmptyBoard))
			await tasks.CreateBoardAsync(Proj, EmptyBoard, "simple", "decision-pending-has-no-ui fixture (no pending nodes)", null, null);
		if (!await tasks.BoardExistsAsync(Proj, FilteredBoard))
			await tasks.CreateBoardAsync(Proj, FilteredBoard, "simple", "decision-pending-has-no-ui fixture (explicit ?decisionPending= only)", null, null);

		var existing = await tasks.GetAsync(Proj, Board, includeClosed: true);
		if (existing.Nodes.Count == 0)
		{
			await tasks.UpsertAsync(Proj, Board,
			[
				new NodePatch { Key = "dpui-pending", Title = "Needs your call", Body = "x", DecisionPending = true },
				new NodePatch { Key = "dpui-plain", Title = "Ordinary task", Body = "x" },
				// A UI-writable toggle target, kept unset so the mutation test controls its own state.
				new NodePatch { Key = "dpui-toggleme", Title = "Toggle target", Body = "x" },
			]);
		}
		var emptyExisting = await tasks.GetAsync(Proj, EmptyBoard, includeClosed: true);
		if (emptyExisting.Nodes.Count == 0)
			await tasks.UpsertAsync(Proj, EmptyBoard, [new NodePatch { Key = "dpui-only-plain", Title = "Nothing waits here", Body = "x" }]);
		var filteredExisting = await tasks.GetAsync(Proj, FilteredBoard, includeClosed: true);
		if (filteredExisting.Nodes.Count == 0)
		{
			await tasks.UpsertAsync(Proj, FilteredBoard,
			[
				new NodePatch { Key = "dpui-pending", Title = "Needs your call", Body = "x", DecisionPending = true },
				new NodePatch { Key = "dpui-plain", Title = "Ordinary task", Body = "x" },
			]);
		}
	}

	public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}

public sealed class BoardDecisionPendingUiTests : IClassFixture<BoardDecisionPendingUiFixture>
{
	const string TestPassword = "test123";
	readonly BoardDecisionPendingUiFixture _fx;

	public BoardDecisionPendingUiTests(BoardDecisionPendingUiFixture fx) => _fx = fx;

	HttpClient NewClient() =>
		_fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

	// Copied from BoardSearchIndexEncodingTests's GetAuthedAsync (itself copied from
	// NavTreeAndDataViewTests) — logs in against a FRESH client (its own independent cookie jar,
	// standing in for "a second browser") and returns the authenticated response for url.
	static async Task<(HttpResponseMessage Response, string AuthCookie)> GetAuthedAsync(HttpClient client, string url)
	{
		var resp = await client.GetAsync(url);
		if (resp.StatusCode != HttpStatusCode.Found)
			return (resp, "");

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
		var final = await client.SendAsync(req);
		return (final, authCookie);
	}

	static string BoardUrl(string board) => $"/ui/{BoardDecisionPendingUiFixture.Ws}/{BoardDecisionPendingUiFixture.Proj}/tasks/{board}";
	static string NodeUrl(string board, string slug) => $"{BoardUrl(board)}/{slug}";

	[Fact]
	public async Task UnfilteredBoard_ShowsBadgeOnThePendingCard_AndStillShowsThePlainOne()
	{
		var client = NewClient();
		var (resp, _) = await GetAuthedAsync(client, BoardUrl(BoardDecisionPendingUiFixture.Board));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-node-key=\"dpui-pending\"");
		html.Should().Contain("data-node-key=\"dpui-plain\"");
		html.Should().Contain("data-testid=\"node-decision-pending-badge\"",
			"the pending node's card must carry a visible badge without opening it");
	}

	[Fact]
	public async Task FilteredBoard_OmitsThePlainNodesMarkupEntirely_NotMerelyHiddenByCss()
	{
		var client = NewClient();
		var (resp, _) = await GetAuthedAsync(client, BoardUrl(BoardDecisionPendingUiFixture.FilteredBoard) + "?decisionPending=true");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-node-key=\"dpui-pending\"");
		html.Should().NotContain("data-node-key=\"dpui-plain\"",
			"a REAL server predicate never ships the excluded node's markup at all — a display-only " +
			"Hidden/display:none row would still contain this string");
	}

	// THE reverse-direction acceptance bullet.
	[Fact]
	public async Task FilteredBoard_WithNoPendingNodes_RendersEmpty_NotTheFullBoard()
	{
		var client = NewClient();
		var (resp, _) = await GetAuthedAsync(client, BoardUrl(BoardDecisionPendingUiFixture.EmptyBoard) + "?decisionPending=true");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().NotContain("data-node-key=\"dpui-only-plain\"");
		html.Should().Contain("data-testid=\"board-empty\"",
			"an enabled filter with zero matches must render the board's own empty state, not fall back to showing everything");
	}

	// live-verification finding (sticky-filter-hides-board-silently): a board that genuinely has an
	// UNFLAGGED node (so it is not "actually" empty at all) must never say so ambiguously — the
	// filter's own ON state has to stay visible in the SAME response that says nothing matched, or
	// a persisted (board-view-cross-device-style) `decisionPending=true` from a past visit hides a
	// live board with zero on-page evidence that anything is filtered at all.
	[Fact]
	public async Task FilteredBoard_WithNoPendingNodes_StillShowsTheToggleOn_AndNamesTheFilterInTheEmptyMessage()
	{
		var client = NewClient();
		var (resp, _) = await GetAuthedAsync(client, BoardUrl(BoardDecisionPendingUiFixture.EmptyBoard) + "?decisionPending=true");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// The board is NOT genuinely empty — dpui-only-plain lives there, unflagged — so the
		// generic "no active task nodes" wording (indistinguishable from an actually-empty board)
		// must not appear; the filter must be independently, visibly ON on the same page.
		html.Should().Contain("data-testid=\"board-decision-pending-toggle\"",
			"the toggle itself must render even when it narrowed the board to zero rows");
		html.Should().Contain("data-decision-pending=\"true\"",
			"and it must show as ON, not just present");
		html.Should().NotContain("This board has no active task nodes.",
			"that phrasing reads as \"nothing lives here\" — indistinguishable from a genuinely empty board — when the truth is a real node exists and the filter hid it");
	}

	// THE shareable-link acceptance bullet: a totally independent context (its own cookie jar, its
	// own login) hitting the exact same `?decisionPending=true` URL must land on the exact same
	// filtered board on its FIRST response — no shared client-side state of any kind is involved.
	[Fact]
	public async Task SameFilteredUrl_FromASecondIndependentContext_RendersTheSameFilteredBoard()
	{
		var url = BoardUrl(BoardDecisionPendingUiFixture.FilteredBoard) + "?decisionPending=true";

		var clientA = NewClient();
		var (respA, _) = await GetAuthedAsync(clientA, url);
		var htmlA = await respA.Content.ReadAsStringAsync();

		// A brand-new client — no cookies, no login state carried over from clientA — is "a
		// different browser" for every purpose this feature cares about (the filter is pure
		// server-side, driven only by the URL).
		var clientB = NewClient();
		var (respB, _) = await GetAuthedAsync(clientB, url);
		var htmlB = await respB.Content.ReadAsStringAsync();

		htmlA.Should().Contain("data-node-key=\"dpui-pending\"");
		htmlB.Should().Contain("data-node-key=\"dpui-pending\"");
		htmlA.Should().NotContain("data-node-key=\"dpui-plain\"");
		htmlB.Should().NotContain("data-node-key=\"dpui-plain\"");
	}

	// Extracts the hidden input's `value="..."` for `inputName` from the given `<form ...>` block
	// (formHtml starts at the form's own opening tag). Returns null when the attribute — or the
	// whole input — is ABSENT, which is a legal outcome a browser must also handle (an omitted
	// hidden input simply never rides in the submitted form data).
	static string? ExtractHiddenValue(string formHtml, string inputName)
	{
		var marker = $"name=\"{inputName}\"";
		var inputStart = formHtml.IndexOf(marker, StringComparison.Ordinal);
		if (inputStart < 0) return null;
		var tagEnd = formHtml.IndexOf('>', inputStart);
		var valueMarker = "value=\"";
		var valueStart = formHtml.IndexOf(valueMarker, inputStart, StringComparison.Ordinal);
		if (valueStart < 0 || valueStart > tagEnd) return null; // no value="..." on THIS input tag
		valueStart += valueMarker.Length;
		return formHtml[valueStart..formHtml.IndexOf('"', valueStart)];
	}

	// THE red-proof shape the coordinator required after the live-verification finding: this test
	// submits EXACTLY the field values the server's own rendered form carries — never a value this
	// test invents — because a hand-assembled POST (the first draft's `pending.ToString()`) cannot
	// observe a broken hidden input's rendered value and would have stayed green through the actual
	// production defect (Razor's bare-bool "minimized attribute" trap: `value="@(someBool)"` renders
	// literal `value="value"` when true and OMITS the attribute when false — see TaskBoardNode.cshtml's
	// own comment on the fix). A form field this test cannot find is passed through as ABSENT from
	// the POST body, exactly like a real browser would if the markup never emitted it.
	static async Task<(string Html, string AuthCookie, string AfCookie, string Token)> GetNodePageAsync(HttpClient client, string nodeUrl)
	{
		var (resp, authCookie) = await GetAuthedAsync(client, nodeUrl);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		var tokenStart = html.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var tokenValueStart = html.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var token = html[tokenValueStart..html.IndexOf('"', tokenValueStart)];
		var afCookie = resp.Headers.GetValues("Set-Cookie")
			.First(c => c.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase))
			.Split(';')[0];
		return (html, authCookie, afCookie, token);
	}

	static async Task<HttpResponseMessage> SubmitDecisionPendingFormAsync(HttpClient client, string nodeUrl, string html, string authCookie, string afCookie, string token)
	{
		var formStart = html.IndexOf("data-testid=\"node-decision-pending-form\"", StringComparison.Ordinal);
		formStart.Should().BeGreaterThan(-1, "the decision-pending form itself must be present on the node page");
		var formEnd = html.IndexOf("</form>", formStart, StringComparison.Ordinal);
		var formHtml = html[formStart..formEnd];

		// Whatever the server actually rendered — including nothing, if the field is missing.
		var version = ExtractHiddenValue(formHtml, "version");
		var pending = ExtractHiddenValue(formHtml, "pending");
		version.Should().NotBeNull("the form must always carry the concurrency baseline");

		var fields = new Dictionary<string, string> { ["version"] = version!, ["__RequestVerificationToken"] = token };
		if (pending is not null) fields["pending"] = pending; // omitted entirely when the server didn't render it — same as a real <form> submit

		var req = new HttpRequestMessage(HttpMethod.Post, $"{nodeUrl}?handler=DecisionPending");
		req.Headers.Add("Cookie", $"{authCookie}; {afCookie}");
		req.Content = new FormUrlEncodedContent(fields);
		return await client.SendAsync(req);
	}

	[Fact]
	public async Task NodeDetailPage_TogglingFromUi_FlipsTheFlag_VisibleThroughGetNodeAsync()
	{
		var client = NewClient();
		var nodeUrl = NodeUrl(BoardDecisionPendingUiFixture.Board, "dpui-toggleme");

		var (html1, authCookie, afCookie, token) = await GetNodePageAsync(client, nodeUrl);
		html1.Should().NotContain("data-testid=\"node-decision-pending-badge\"", "starts un-flagged");

		// Click 1: "mark waiting on me" — submits EXACTLY what this render's form carries.
		var setResp = await SubmitDecisionPendingFormAsync(client, nodeUrl, html1, authCookie, afCookie, token);
		setResp.StatusCode.Should().Be(HttpStatusCode.Redirect, "a successful ApplyAsync PRGs back to the canonical node URL");

		using (var scope = _fx.Factory.Services.CreateScope())
		{
			var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
			var afterSet = await tasks.GetNodeBySlugAsync(BoardDecisionPendingUiFixture.Proj, BoardDecisionPendingUiFixture.Board, "dpui-toggleme");
			afterSet.Should().NotBeNull();
			afterSet!.Node.DecisionPending.Should().BeTrue(
				"clicking \"mark waiting on me\" as the server's own form actually submits it must set the flag — " +
				"this is the exact assertion that stayed green under the production defect when the POST body was hand-built instead of scraped off the real form");
		}

		// Click 2: re-fetch (the form now reflects the NEW state) and submit AGAIN exactly as
		// rendered — this is "clear decision flag", which must ALSO go through the real form.
		var (html2, authCookie2, afCookie2, token2) = await GetNodePageAsync(client, nodeUrl);
		html2.Should().Contain("data-testid=\"node-decision-pending-badge\"", "now flagged, so the badge must show without opening anything else");
		var clearResp = await SubmitDecisionPendingFormAsync(client, nodeUrl, html2, authCookie2, afCookie2, token2);
		clearResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

		using (var scope = _fx.Factory.Services.CreateScope())
		{
			var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
			var afterClear = await tasks.GetNodeBySlugAsync(BoardDecisionPendingUiFixture.Proj, BoardDecisionPendingUiFixture.Board, "dpui-toggleme");
			afterClear!.Node.DecisionPending.Should().BeFalse("the same UI door, driven the same way, clears it too");
		}
	}
}
