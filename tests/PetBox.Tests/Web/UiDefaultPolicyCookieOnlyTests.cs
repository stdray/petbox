using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// apikey-principal-authz-cluster, finding 1: an API KEY used to RENDER the Razor pages that sit
// under a bare [Authorize].
//
// The mechanism, all of it in Program.cs: DefaultScheme is the "Smart" policy scheme, whose
// ForwardDefaultSelector routes any request carrying X-Api-Key to the ApiKey handler; the framework's
// stock DEFAULT authorization policy is RequireAuthenticatedUser() with NO scheme named, so it
// accepted whatever that handler produced. The ApiKey handler emits `project`, `scopes`, `host`,
// `project_default`, `sandbox_only`, `key_name` — and NO `yb:user_id`, no `yb:sysadmin`, no
// `yb:ws_roles`. /ui/me/account is the sharpest case: a page whose entire content is "who you are",
// served with 200 to a principal that is not a user at all. Every component on such a page then has
// to invent its own answer to "there is no user here", and one of them invented the wrong one — the
// NavigationContext.AvailableWorkspaces free pass that showed the whole tenant catalog.
//
// THE FIX IS ONE LINE OF POLICY, NOT SEVEN ATTRIBUTES: SetDefaultPolicy(cookie scheme). A per-page
// [Authorize(Policy = …)] on the seven pages that exist today leaves the hole open for the eighth
// page written tomorrow.
//
// BOTH SIDES ARE ASSERTED HERE ON PURPOSE. "The api key is refused" alone is also what a page that
// refuses EVERYONE looks like, and that is the way this change breaks production. So the same page,
// same fixture, same request: refused for the key, 200 with real content for the cookie.
//
// The api-key half of the tree is proved intact elsewhere and deliberately not re-proved here:
// ShareApiAuthzTests (POST /api/share with X-Api-Key), LogLiveTailTests + LogEventDetailsApiTests
// (the "ApiKeyOrCookie" SSE/details pair), LogIngestClefAuthzTests and OtlpIngestAuthzTests (the
// named "ApiKey" policy). None of them route through the default policy, which is the point.
public sealed class UiDefaultPolicyCookieOnlyFixture : IAsyncLifetime
{
	const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Password = "test123";
	public const string Username = "uipolicyuser";
	public const string ProjectKey = "uipolicyproj";
	public const string ApiKeyValue = "yb_key_uipolicy_probe";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public UiDefaultPolicyCookieOnlyFixture()
	{
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
					});
				});
			});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		// HandleCookies=false so the auth cookie is threaded by hand: a hidden cookie container would
		// make the api-key request below silently ALSO a cookie request, which is the one confusion that
		// would make this whole test meaningless.
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();

		await db.InsertAsync(new Workspace { Key = "uipolicyws", Name = "UiPolicyWs", Description = "", CreatedAt = DateTime.UtcNow });
		var userId = await db.InsertWithInt64IdentityAsync(
			new User { Username = Username, PasswordHash = PasswordHash, CreatedAt = DateTime.UtcNow });
		await db.SeedMemberAsync(userId, "uipolicyws", WorkspaceRole.Admin);

		await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = "uipolicyws", Name = "UiPolicyProj" });
		// A perfectly ordinary, perfectly valid project key — the exploit never needed a special one.
		await db.InsertAsync(new ApiKey
		{
			Key = ApiKeyValue,
			ProjectKey = ProjectKey,
			Scopes = "tasks:read, memory:read",
			CreatedAt = DateTime.UtcNow,
		});
	}

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class UiDefaultPolicyCookieOnlyTests : IClassFixture<UiDefaultPolicyCookieOnlyFixture>
{
	readonly HttpClient _client;

	public UiDefaultPolicyCookieOnlyTests(UiDefaultPolicyCookieOnlyFixture fx) => _client = fx.Client;

	// Every page under a bare [Authorize] as of this commit. Kept as data rather than one hand-picked
	// URL so the assertion is about the POLICY, not about /ui/me/account happening to be closed.
	public static TheoryData<string> BareAuthorizePages() => new()
	{
		"/",                      // Pages/Index.cshtml.cs
		"/ui/search",             // Pages/Search.cshtml.cs
		"/ui/_nav/tree",          // Pages/Nav/Tree.cshtml.cs
		"/ui/me/account",         // Pages/Me/Account.cshtml.cs
		"/ui/me/preferences",     // Pages/Me/Preferences.cshtml.cs
		"/ui/me/security",        // Pages/Me/Security.cshtml.cs
		"/AccessDenied",          // Pages/AccessDenied.cshtml.cs
	};

	static HttpRequestMessage Get(string path, string? apiKey = null, string? cookie = null)
	{
		var req = new HttpRequestMessage(HttpMethod.Get, path);
		if (apiKey is not null) req.Headers.Add("X-Api-Key", apiKey);
		if (cookie is not null) req.Headers.Add("Cookie", cookie);
		return req;
	}

	async Task<string> LoginAsync()
	{
		var loginPage = await _client.GetAsync("/Login");
		var html = await loginPage.Content.ReadAsStringAsync();
		var tokenStart = html.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = html.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var token = html[valueStart..html.IndexOf('"', valueStart)];
		var afCookie = loginPage.Headers.GetValues("Set-Cookie")
			.First(c => c.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase)).Split(';')[0];

		var req = new HttpRequestMessage(HttpMethod.Post, "/Login")
		{
			Content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["username"] = UiDefaultPolicyCookieOnlyFixture.Username,
				["password"] = UiDefaultPolicyCookieOnlyFixture.Password,
				["__RequestVerificationToken"] = token,
			}),
		};
		req.Headers.Add("Cookie", afCookie);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, "the test user must be able to sign in");
		return resp.Headers.GetValues("Set-Cookie")
			.First(c => c.StartsWith(".AspNetCore.Cookies", StringComparison.OrdinalIgnoreCase)).Split(';')[0];
	}

	// SIDE ONE — the hole. A valid API key must not get a Razor page.
	//
	// The refusal shape is the COOKIE handler's challenge (302 -> /Login), not a 401 body: the default
	// policy now names the cookie scheme, cookie auth returns NoResult for a request that has no cookie,
	// and the authorization middleware challenges every scheme the policy names. That is a redirect an
	// api-key client cannot follow anywhere useful, and — the property that matters — no page and no
	// data come back with it.
	[Theory]
	[MemberData(nameof(BareAuthorizePages))]
	public async Task ApiKeyPrincipal_IsRefused_ByEveryBareAuthorizePage(string path)
	{
		using var resp = await _client.SendAsync(Get(path, apiKey: UiDefaultPolicyCookieOnlyFixture.ApiKeyValue));

		resp.StatusCode.Should().NotBe(HttpStatusCode.OK,
			$"an api-key principal has no user id, no roles and no workspace memberships — '{path}' must not render for it");
		resp.StatusCode.Should().Be(HttpStatusCode.Redirect,
			$"the cookie-only default policy challenges through the cookie handler, so '{path}' answers an api key with a redirect to the sign-in page");
		// Absolute, not relative — the cookie handler builds the challenge URL off the request's own
		// scheme/host — so this matches the path segment rather than the start of the string.
		resp.Headers.Location!.ToString().Should().Contain("/Login?ReturnUrl=",
			"the challenge is the cookie scheme's LoginPath, carrying the refused path back");
	}

	// SIDE TWO — the regression this change could cause. The SAME page, the SAME fixture, a real signed-in
	// user: still 200, still the real page. Without this half, a policy that refuses everybody would pass.
	[Fact]
	public async Task CookieUser_StillGetsTheAccountPage_Rendered()
	{
		var authCookie = await LoginAsync();

		using var resp = await _client.SendAsync(Get("/ui/me/account", cookie: authCookie));

		resp.StatusCode.Should().Be(HttpStatusCode.OK,
			"narrowing the default policy to the cookie scheme must not touch the principal it was narrowed TO");
		var body = await resp.Content.ReadAsStringAsync();
		body.Should().Contain(UiDefaultPolicyCookieOnlyFixture.Username,
			"the page must be the real /ui/me/account render — it prints the caller's own username, which is the identity an api key does not have");
	}

	// The other bare-[Authorize] pages, for the cookie principal, on the same sweep as the refusal
	// theory: a redirect to /Login here would mean the narrowing closed a page on real users too.
	//
	// The expected status is spelled out per page because two of them are not 200 for reasons that have
	// nothing to do with authorization — /AccessDenied is the refusal page and deliberately sets 403 on
	// its own render, and "/" redirects a signed-in user onto their landing workspace. Asserting a flat
	// 200 would have forced them out of the sweep; asserting the EXACT status keeps them in it, and any
	// of them turning into a /Login redirect still fails.
	[Theory]
	[InlineData("/ui/search", 200)]
	[InlineData("/ui/me/account", 200)]
	[InlineData("/ui/me/preferences", 200)]
	[InlineData("/ui/me/security", 200)]
	[InlineData("/AccessDenied", 403)]
	public async Task CookieUser_IsNotRefused_ByTheNarrowedDefault(string path, int expectedStatus)
	{
		var authCookie = await LoginAsync();

		using var resp = await _client.SendAsync(Get(path, cookie: authCookie));

		((int)resp.StatusCode).Should().Be(expectedStatus,
			$"'{path}' is under the narrowed default policy and a cookie-authenticated user satisfies it");
	}
}
