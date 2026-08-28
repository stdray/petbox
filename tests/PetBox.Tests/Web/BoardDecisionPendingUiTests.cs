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

	[Fact]
	public async Task NodeDetailPage_TogglingFromUi_FlipsTheFlag_VisibleThroughGetNodeAsync()
	{
		var client = NewClient();
		var nodeUrl = NodeUrl(BoardDecisionPendingUiFixture.Board, "dpui-toggleme");
		var (getResp, authCookie) = await GetAuthedAsync(client, nodeUrl);
		getResp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await getResp.Content.ReadAsStringAsync();
		html.Should().NotContain("data-testid=\"node-decision-pending-badge\"", "starts un-flagged");

		// Antiforgery needs BOTH the token AND its matching cookie (AgentKeyEditTests.
		// ExtractAntiforgery's pattern) — the auth cookie alone 400s a POST, which is exactly what
		// the first draft of this test did (red-proof of the harness, not the feature: fixed before
		// this test was ever used to prove the feature itself).
		var tokenStart = html.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var tokenValueStart = html.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var token = html[tokenValueStart..html.IndexOf('"', tokenValueStart)];
		var afCookie = getResp.Headers.GetValues("Set-Cookie")
			.First(c => c.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase))
			.Split(';')[0];
		var formStart = html.IndexOf("data-testid=\"node-decision-pending-form\"", StringComparison.Ordinal);
		var versionMarker = "name=\"version\" value=\"";
		var versionStart = html.IndexOf(versionMarker, formStart, StringComparison.Ordinal) + versionMarker.Length;
		var version = html[versionStart..html.IndexOf('"', versionStart)];

		async Task<HttpResponseMessage> PostToggleAsync(bool pending, string ver)
		{
			var req = new HttpRequestMessage(HttpMethod.Post, $"{nodeUrl}?handler=DecisionPending");
			req.Headers.Add("Cookie", $"{authCookie}; {afCookie}");
			req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["pending"] = pending.ToString(),
				["version"] = ver,
				["__RequestVerificationToken"] = token,
			});
			return await client.SendAsync(req);
		}

		var setResp = await PostToggleAsync(true, version);
		setResp.StatusCode.Should().Be(HttpStatusCode.Redirect, "a successful ApplyAsync PRGs back to the canonical node URL");

		using (var scope = _fx.Factory.Services.CreateScope())
		{
			var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
			var afterSet = await tasks.GetNodeBySlugAsync(BoardDecisionPendingUiFixture.Proj, BoardDecisionPendingUiFixture.Board, "dpui-toggleme");
			afterSet.Should().NotBeNull();
			afterSet!.Node.DecisionPending.Should().BeTrue("the SAME read tasks_node_get wraps must see the flag the UI just set");

			// Clear it back — same door, opposite value, next version.
			var clearResp = await PostToggleAsync(false, afterSet.Node.Version.ToString());
			clearResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
			var afterClear = await tasks.GetNodeBySlugAsync(BoardDecisionPendingUiFixture.Proj, BoardDecisionPendingUiFixture.Board, "dpui-toggleme");
			afterClear!.Node.DecisionPending.Should().BeFalse("the same UI door clears it too");
		}
	}
}
