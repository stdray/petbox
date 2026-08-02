using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// ui-back-nav-no-bfcache: the browser Back button from a UI page went through a full server
// re-render instead of restoring from bfcache. Confirmed on live prod (owner scenario, replayed
// under sysadmin via playwright-cli — see the card) and reproduced here at the HTTP level: every
// authenticated GET UI page carried `Cache-Control: no-cache, no-store` + `Pragma: no-cache`. This
// is ASP.NET Core Antiforgery's own signature — it sets exactly that pair whenever a page renders
// @Html.AntiForgeryToken(), and _Layout.cshtml does that unconditionally (the sign-out POST form
// in the header), so EVERY authenticated page inherited it even though no PetBox code sets it
// anywhere (grepped: only Error.cshtml.cs and LogApi.cs set a cache-control header at all, and
// neither is this one). `Cache-Control: no-store` is the one thing Chrome/Firefox bfcache checks
// on the top-level document — its presence disqualifies the page outright, independent of
// `Pragma`/`Vary`/anything else.
//
// The fix (Program.cs, the app.Use registered just above app.UseAuthentication()) rewrites that
// header to `private, no-cache` for GET/HEAD text/html UI responses only, via a single
// Response.OnStarting callback registered once, above every page/endpoint — not per-PageModel. It
// does NOT touch antiforgery TOKEN VALIDATION (a request-side check, unrelated code path) and does
// NOT touch any endpoint that declares its own [ResponseCache] (Error.cshtml keeps its deliberate
// no-store). This fixture asserts that HTTP-visible contract so `no-store` cannot silently return.
//
// NOT proof that a real browser actually restores from bfcache — bfcache is a browser-internal
// decision Playwright cannot observe (its own Chromium instances run with bfcache force-disabled;
// confirmed on this card via a control run against a known-good no-no-store page, which also came
// back non-restored). The one authoritative check is Chrome DevTools → Application → Back/forward
// cache → "Test back/forward cache" in a real, non-automated browser. This test's job is narrower
// and fully within reach: prove the header PetBox controls is the one Chrome's bfcache algorithm
// actually gates on, and that it no longer ships.
public sealed class UiPageBfcacheHeaderFixture : IAsyncLifetime
{
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string TestPassword = "test123";
	public const string Ws = "bfws";
	public const string Proj = "bfproj";

	WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;
	public string AuthCookie { get; private set; } = string.Empty;

	public UiPageBfcacheHeaderFixture()
	{
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

		// HandleCookies=false (LoginAuthFixture's pattern): every request below attaches cookies
		// manually, so an automatic cookie container would only silently make the "unauthenticated"
		// /Login checks see the fixture's OWN auth cookie and redirect instead of rendering the form.
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
			HandleCookies = false,
		});

		// Login once for the whole fixture (same flow as SessionsSearchUiTests) — every test below
		// only reads pages, nothing mutates auth state.
		var loginPage = await Client.GetAsync("/Login");
		var loginHtml = await loginPage.Content.ReadAsStringAsync();
		var tokenStart = loginHtml.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = loginHtml.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var valueEnd = loginHtml.IndexOf('"', valueStart);
		var token = loginHtml[valueStart..valueEnd];
		var loginCookies = loginPage.Headers.GetValues("Set-Cookie").ToList();

		var loginReq = new HttpRequestMessage(HttpMethod.Post, "/Login?returnUrl=%2F");
		loginReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = "admin",
			["password"] = TestPassword,
			["returnUrl"] = "/",
			["__RequestVerificationToken"] = token,
		});
		foreach (var c in loginCookies) loginReq.Headers.Add("Cookie", c.Split(';')[0]);
		var loginResp = await Client.SendAsync(loginReq);
		AuthCookie = loginResp.Headers.GetValues("Set-Cookie").First().Split(';')[0];
	}

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class UiPageBfcacheHeaderTests(UiPageBfcacheHeaderFixture fx) : IClassFixture<UiPageBfcacheHeaderFixture>
{
	readonly HttpClient _client = fx.Client;

	async Task<HttpResponseMessage> GetAuthedAsync(string url)
	{
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", fx.AuthCookie);
		return await _client.SendAsync(req);
	}

	[Fact]
	public async Task Authenticated_UI_page_is_not_shipped_no_store()
	{
		var url = $"/ui/{UiPageBfcacheHeaderFixture.Ws}/{UiPageBfcacheHeaderFixture.Proj}/sessions"
			+ "?q=Vibe&agent=&sortBy=updated&sortDesc=true&size=40";
		using var resp = await GetAuthedAsync(url);

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		resp.Content.Headers.ContentType!.MediaType.Should().Be("text/html");

		var cacheControl = resp.Headers.CacheControl?.ToString() ?? string.Empty;
		cacheControl.Should().NotContain("no-store",
			"a `no-store` document response is unconditionally excluded from Chrome/Firefox bfcache — "
			+ "the regression this card exists to prevent from coming back silently (it was ASP.NET "
			+ "Core Antiforgery's own default whenever @Html.AntiForgeryToken() renders, which "
			+ "_Layout.cshtml's sign-out form does on every authenticated page)");
		resp.Headers.Pragma.Should().BeEmpty(
			"the antiforgery default also ships Pragma: no-cache alongside no-store; both must go "
			+ "together or a legacy/careful UA could still treat the page as non-cacheable");
	}

	[Fact]
	public async Task Login_page_unauthenticated_is_also_not_shipped_no_store()
	{
		// /Login itself renders its OWN antiforgery-tokened form — same signature, reachable before
		// any session exists, so the fix must not be scoped to "authenticated pages only".
		using var resp = await _client.GetAsync("/Login");

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var cacheControl = resp.Headers.CacheControl?.ToString() ?? string.Empty;
		cacheControl.Should().NotContain("no-store");
	}

	[Fact]
	public async Task Error_page_keeps_its_own_explicit_no_store()
	{
		// Error.cshtml.cs declares [ResponseCache(NoStore = true)] deliberately (it is re-executed for
		// a failed/refused request and must never be replayed from cache) — the bfcache convention
		// must respect that explicit, page-level opt-out rather than blanket-overriding every page.
		using var resp = await GetAuthedAsync("/Error");

		var cacheControl = resp.Headers.CacheControl?.ToString() ?? string.Empty;
		cacheControl.Should().Contain("no-store");
	}

	[Fact]
	public async Task Antiforgery_token_validation_on_POST_is_unaffected()
	{
		// The response-header rewrite must never be mistaken for (or accidentally become) a
		// weakening of antiforgery's REQUEST-side validation — a POST missing its token must still
		// be rejected, exactly as before this card.
		var loginPage = await _client.GetAsync("/Login");
		var cookies = loginPage.Headers.GetValues("Set-Cookie").ToList();

		var req = new HttpRequestMessage(HttpMethod.Post, "/Login?returnUrl=%2F");
		req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = "admin",
			["password"] = UiPageBfcacheHeaderFixture.TestPassword,
			["returnUrl"] = "/",
			// deliberately NO __RequestVerificationToken field
		});
		foreach (var c in cookies) req.Headers.Add("Cookie", c.Split(';')[0]);
		using var resp = await _client.SendAsync(req);

		resp.StatusCode.Should().NotBe(HttpStatusCode.OK,
			"a POST missing its antiforgery token must still be rejected — this card only rewrites a "
			+ "response header on GET/HEAD, never request-side antiforgery validation");
		resp.StatusCode.Should().NotBe(HttpStatusCode.Found,
			"a redirect here would mean the login succeeded without a valid antiforgery token");
	}
}
