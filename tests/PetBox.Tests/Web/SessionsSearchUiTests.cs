using System.Net;
using System.Text.RegularExpressions;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Sessions.Contract;

namespace PetBox.Tests.Web;

// ui-search-shared-panel: Sessions.cshtml had NO dedicated page-level test before this card
// (SessionSearchServiceTests/SessionSearchCursorTests exercise the SERVICE, never the Razor
// page) — this is the "test on each of the three consumers" the card requires for the one
// consumer that had none. Drives the REAL page (WebApplicationFactory<Program>, real HTTP +
// cookie login + Razor rendering) to confirm the shared _SearchQueryBox/_SearchPageSizeSelect
// partials this card introduced render correctly and the page's own keyset listing walk still
// visits every session exactly once — the regression guard for the query-box/size-select
// extraction out of Sessions.cshtml's previously-inline markup.
public sealed class SessionsSearchUiFixture : IAsyncLifetime
{
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Ws = "sessui-ws";
	public const string Proj = "sessui-proj";

	WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public SessionsSearchUiFixture()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
					["Host:BackgroundServices"] = "false",
					["Features:Tasks"] = "true",
					["Admin:Username"] = "admin",
					["Admin:PasswordHash"] = TestPasswordHash,
				}));
			});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		using (var db = new PetBoxDb(PetBoxDb.CreateOptions(cs)))
			if (!db.Projects.Any(p => p.Key == Proj))
				db.Insert(new Project { Key = Proj, WorkspaceKey = Ws, Name = Proj, Description = "" });

		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = Factory.Services.CreateScope();
		var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
		for (var i = 0; i < 45; i++)
			await sessions.UpsertAsync(Proj, $"sess-{i:0000}", "claude-code",
				[new SessionMessageInput("user", $"message {i}")]);
	}

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class SessionsSearchUiTests(SessionsSearchUiFixture fx) : IClassFixture<SessionsSearchUiFixture>
{
	const string TestPassword = "test123";
	readonly HttpClient _client = fx.Client;

	async Task<HttpResponseMessage> GetAuthedAsync(string url)
	{
		var resp = await _client.GetAsync(url);
		if (resp.StatusCode != HttpStatusCode.Found) return resp;

		var loginPage = await _client.GetAsync("/Login");
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

		var loginResp = await _client.SendAsync(loginReq);
		var authCookie = loginResp.Headers.GetValues("Set-Cookie").First();
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", authCookie.Split(';')[0]);
		return await _client.SendAsync(req);
	}

	static List<string> ExtractSessionIds(string html) =>
		[.. Regex.Matches(html, "data-testid=\"session-card\" data-session-id=\"(?<id>[^\"]+)\"").Select(m => m.Groups["id"].Value)];

	static string? ExtractNextCursor(string html)
	{
		var m = Regex.Match(html, "<a href=\"(?<h>[^\"]*)\"[^>]*data-testid=\"sessions-next\"");
		if (!m.Success) return null;
		var href = WebUtility.HtmlDecode(m.Groups["h"].Value);
		var cm = Regex.Match(href, "[?&]cursor=(?<c>[^&]+)");
		return cm.Success ? Uri.UnescapeDataString(cm.Groups["c"].Value) : null;
	}

	[Fact]
	public async Task SharedQueryBoxAndSizeSelect_RenderWithThisPagesOwnTestidsAndValue()
	{
		using var resp = await GetAuthedAsync($"/ui/{SessionsSearchUiFixture.Ws}/{SessionsSearchUiFixture.Proj}/sessions?q=probe123&size=20");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"sessions-search-input\"", "the shared _SearchQueryBox partial must still carry Sessions' own testid");
		html.Should().Contain("value=\"probe123\"", "the shared partial must echo the bound query value back");
		html.Should().Contain("data-testid=\"sessions-search-size\"", "the shared _SearchPageSizeSelect partial must still carry Sessions' own testid");
		html.Should().Contain("<option value=\"20\" selected", "the requested size must render selected through the shared select");
	}

	[Fact]
	public async Task Listing_WalkedPageByPage_VisitsEverySessionExactlyOnce()
	{
		var seen = new List<string>();
		string? cursor = null;
		var pages = 0;
		do
		{
			var url = $"/ui/{SessionsSearchUiFixture.Ws}/{SessionsSearchUiFixture.Proj}/sessions"
				+ (cursor is null ? "" : $"?cursor={Uri.EscapeDataString(cursor)}");
			using var resp = await GetAuthedAsync(url);
			resp.StatusCode.Should().Be(HttpStatusCode.OK);
			var html = await resp.Content.ReadAsStringAsync();
			var pageIds = ExtractSessionIds(html);
			pageIds.Should().NotBeEmpty($"page {pages} must render at least one row");
			seen.AddRange(pageIds);
			cursor = ExtractNextCursor(html);
			pages++;
			pages.Should().BeLessThan(20, "a bug here must not spin forever");
		} while (cursor is not null);

		seen.Distinct(StringComparer.Ordinal).Should().HaveCount(45, "every seeded session must be reachable exactly once");
	}
}
