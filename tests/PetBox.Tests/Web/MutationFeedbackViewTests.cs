using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// ui-mutation-feedback-consistency (render side): drives the shared success-notice + confirm
// conventions end-to-end through the real Razor/TempData pipeline —
//  * a create POST Post/Redirect/Gets to a clean URL (no ?handler=) and the notice renders on
//    the landing page after the redirect (proves the layout wiring + TempData cookie round-trip);
//  * a destructive confirm names the entity and warns about the cascade.
public sealed class MutationFeedbackViewFixture : IAsyncLifetime
{
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public MutationFeedbackViewFixture()
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
					["Admin:Username"] = "admin",
					["Admin:PasswordHash"] = TestPasswordHash,
				}));
			});
	}

	public Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class MutationFeedbackViewTests : IClassFixture<MutationFeedbackViewFixture>
{
	const string TestPassword = "test123";

	readonly WebApplicationFactory<Program> _factory;
	readonly HttpClient _client;

	public MutationFeedbackViewTests(MutationFeedbackViewFixture fx)
	{
		_factory = fx.Factory;
		_client = fx.Client;
	}

	[Fact]
	public async Task WorkspaceCreate_RedirectsToCleanUrl_AndRendersSuccessNoticeAfterRedirect()
	{
		var jar = new CookieJar();
		await LogInAsync(jar);

		// GET the workspaces page for its anti-forgery token, then POST a create.
		var formPage = await GetAsync("/ui/admin/sys/workspaces", jar);
		var formToken = ExtractToken(await formPage.Content.ReadAsStringAsync());
		var create = await PostAsync("/ui/admin/sys/workspaces?handler=Create", jar, new()
		{
			["Key"] = "noticews",
			["Name"] = "Notice WS",
			["Description"] = "",
			["__RequestVerificationToken"] = formToken,
		});

		// PRG: 302 to the clean workspaces URL — no lingering ?handler=.
		create.StatusCode.Should().Be(HttpStatusCode.Found);
		var location = create.Headers.Location!.ToString();
		location.Should().NotContain("handler=");

		// Follow the redirect carrying the TempData cookie — the shared notice renders once.
		var landing = await GetAsync(location, jar);
		landing.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await landing.Content.ReadAsStringAsync();
		html.Should().MatchRegex("data-testid=\"notice-success\"[\\s\\S]*?noticews[\\s\\S]*?created");
	}

	[Fact]
	public async Task UsersPage_DeleteConfirm_NamesUserAndWarnsAboutCascade()
	{
		using (var scope = _factory.Services.CreateScope())
		{
			using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
			if (!db.Users.Any(u => u.Username == "victim"))
				db.Insert(new User { Username = "victim", PasswordHash = "x", CreatedAt = DateTime.UtcNow });
		}

		var jar = new CookieJar();
		await LogInAsync(jar);
		var resp = await GetAsync("/ui/admin/sys/users", jar);

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("Delete user victim? This removes the account and all its workspace memberships.");
	}

	async Task LogInAsync(CookieJar jar)
	{
		var login = await GetAsync("/Login", jar);
		var token = ExtractToken(await login.Content.ReadAsStringAsync());
		var resp = await PostAsync("/Login?returnUrl=/", jar, new()
		{
			["username"] = "admin",
			["password"] = TestPassword,
			["returnUrl"] = "/",
			["__RequestVerificationToken"] = token,
		});
		resp.StatusCode.Should().Be(HttpStatusCode.Found, "valid admin credentials sign in");
	}

	async Task<HttpResponseMessage> GetAsync(string url, CookieJar jar)
	{
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		jar.Apply(req);
		var resp = await _client.SendAsync(req);
		jar.Capture(resp);
		return resp;
	}

	async Task<HttpResponseMessage> PostAsync(string url, CookieJar jar, Dictionary<string, string> fields)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
		jar.Apply(req);
		var resp = await _client.SendAsync(req);
		jar.Capture(resp);
		return resp;
	}

	static string ExtractToken(string html)
	{
		var idx = html.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = html.IndexOf("value=\"", idx, StringComparison.Ordinal) + 7;
		var valueEnd = html.IndexOf('"', valueStart);
		return html[valueStart..valueEnd];
	}

	// Minimal accumulating cookie jar (the test client has no CookieContainer): keeps the latest
	// value per cookie name across requests so anti-forgery, auth and TempData cookies all ride along.
	sealed class CookieJar
	{
		readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

		public void Capture(HttpResponseMessage resp)
		{
			if (!resp.Headers.TryGetValues("Set-Cookie", out var values)) return;
			foreach (var raw in values)
			{
				var pair = raw.Split(';', 2)[0];
				var eq = pair.IndexOf('=');
				if (eq <= 0) continue;
				_cookies[pair[..eq]] = pair[(eq + 1)..];
			}
		}

		public void Apply(HttpRequestMessage req)
		{
			if (_cookies.Count == 0) return;
			req.Headers.Add("Cookie", string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}")));
		}
	}
}
