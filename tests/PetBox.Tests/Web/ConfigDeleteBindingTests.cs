using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// Repro for the workspace-config "Del" button doing nothing (the llm-router stores its
// registry as $system-workspace config bindings; deleting one from /ui/$system/config
// returned 200 and left the row intact). Drives the actual rendered delete <form> through
// HTTP — antiforgery + cookie + link-generated action — which the page-model unit tests
// (LlmAdminPageTests / ConfigPipelineTests) bypass.
// Single-test class: a shared per-class fixture would save nothing, so it keeps the
// per-test host. It left the serialized WebAppFactory collection though: the per-class
// connection string moved from the process-global CONNECTIONSTRINGS__PETBOX env var to
// in-memory config, and no env var is written at all.
public sealed class ConfigDeleteBindingTests : IAsyncLifetime
{
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _client = null!;

	const string WriteKey = "yb_key_test_config_delete_write";
	const string TestPassword = "test123";
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public ConfigDeleteBindingTests()
	{
		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Host:BackgroundServices"] = "false",
						["Features:Config"] = "true",
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = TestPasswordHash,
					});
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		_client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		await db.InsertAsync(new ApiKey
		{
			Key = WriteKey,
			ProjectKey = "$system",
			Scopes = "config:read,config:write",
			CreatedAt = DateTime.UtcNow,
		});
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
	}

	[Fact]
	public async Task Delete_FromWorkspaceConfigPage_RemovesBinding()
	{
		// 1. Seed a binding in the $system workspace config (same shape as the llm-router rows).
		var path = $"llm/secret/repro-{Guid.NewGuid():N}";
		var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/config/$system/bindings");
		createReq.Headers.Add("X-Api-Key", WriteKey);
		createReq.Content = JsonContent.Create(new { path, value = "v", tags = "ws:$system" });
		long bindingId;
		using (var createResp = await _client.SendAsync(createReq))
		{
			createResp.StatusCode.Should().Be(HttpStatusCode.OK, "binding must be created to delete it");
			using var doc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
			bindingId = doc.RootElement.GetProperty("id").GetInt64();
		}

		var authCookie = await LoginAsync("/ui/$system/config");

		// 2. Render the page. The delete form's action must be a REAL URL carrying the handler +
		//    id — the bug was asp-page link generation emitting action="" for these custom-routed
		//    pages, so the POST hit no handler (200 no-op).
		var (html, pageCookies) = await GetWithCookiesAsync("/ui/$system/config", authCookie);
		html.Should().Contain(path, "the seeded binding row must render on the workspace config page");
		var deleteUrl = $"/ui/$system/config?handler=Delete&id={bindingId}";
		html.Should().Contain(WebUtility.HtmlEncode(deleteUrl),
			"the Del form action must target the Delete handler with the binding id (not an empty action)");

		var token = ExtractAntiforgeryToken(html);

		// 3. Replay the delete exactly as the browser would: form POST with auth + antiforgery cookies.
		var delReq = new HttpRequestMessage(HttpMethod.Post, deleteUrl);
		foreach (var c in new[] { authCookie }.Concat(pageCookies))
			delReq.Headers.Add("Cookie", c);
		delReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["__RequestVerificationToken"] = token,
		});
		using var delResp = await _client.SendAsync(delReq);
		delResp.StatusCode.Should().Be(HttpStatusCode.Found, "a successful delete redirects (302), not renders (200)");

		// 4. The row must be gone on a fresh render.
		var (after, _) = await GetWithCookiesAsync("/ui/$system/config", authCookie);
		after.Should().NotContain(path, "the binding must actually be soft-deleted");
	}

	// --- helpers ---

	// Logs in and returns the auth cookie (name=value), following the antiforgery dance.
	async Task<string> LoginAsync(string returnUrl)
	{
		var loginPage = await _client.GetAsync("/Login");
		var loginHtml = await loginPage.Content.ReadAsStringAsync();
		var token = ExtractAntiforgeryToken(loginHtml);
		var loginCookies = loginPage.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).ToList();

		var loginReq = new HttpRequestMessage(HttpMethod.Post, "/Login?returnUrl=" + Uri.EscapeDataString(returnUrl));
		loginReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = "admin",
			["password"] = TestPassword,
			["returnUrl"] = returnUrl,
			["__RequestVerificationToken"] = token,
		});
		foreach (var c in loginCookies) loginReq.Headers.Add("Cookie", c);

		using var loginResp = await _client.SendAsync(loginReq);
		loginResp.StatusCode.Should().Be(HttpStatusCode.Redirect, "admin login should succeed");
		return loginResp.Headers.GetValues("Set-Cookie").First().Split(';')[0];
	}

	async Task<(string Html, List<string> Cookies)> GetWithCookiesAsync(string url, string authCookie)
	{
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", authCookie);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var cookies = resp.Headers.TryGetValues("Set-Cookie", out var sc)
			? sc.Select(c => c.Split(';')[0]).ToList()
			: [];
		return (await resp.Content.ReadAsStringAsync(), cookies);
	}

	static string ExtractAntiforgeryToken(string html)
	{
		var m = Regex.Match(html,
			"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.IgnoreCase);
		if (!m.Success)
			m = Regex.Match(html, "value=\"([^\"]+)\"[^>]*name=\"__RequestVerificationToken\"", RegexOptions.IgnoreCase);
		return m.Success ? m.Groups[1].Value : string.Empty;
	}
}
