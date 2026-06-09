using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

[Collection("WebAppFactory")]
public sealed class ConfigPipelineTests : IAsyncLifetime
{
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _client = null!;

	const string ReadKey = "yb_key_system_internal";
	const string WriteKey = "yb_key_test_config_write";
	const string TestPassword = "test123";
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public ConfigPipelineTests()
	{
		Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__PETBOX", $"Data Source={Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db")};Cache=Shared");
		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["Features:Config"] = "true",
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = TestPasswordHash,
					});
				});
			});
	}

	public async Task InitializeAsync()
	{
		var __testCs = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(_factory.Services).GetConnectionString("PetBox")!;
		TestSchema.Core(__testCs);
		_client = _factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
		});

		using var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		try
		{
			await db.InsertAsync(new ApiKey
			{
				Key = WriteKey,
				ProjectKey = "$system",
				Scopes = "config:read,config:write",
				CreatedAt = DateTime.UtcNow,
			});
		}
		catch (Microsoft.Data.Sqlite.SqliteException)
		{
			// shared in-memory DB — key already inserted by another test instance
		}
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
		Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__PETBOX", null);
	}

	async Task<HttpResponseMessage> GetPageAsync(string url)
	{
		var resp = await _client.GetAsync(url);
		if (resp.StatusCode == HttpStatusCode.Found)
		{
			// Get anti-forgery token from login page
			var loginPage = await _client.GetAsync("/Login");
			var loginHtml = await loginPage.Content.ReadAsStringAsync();
			var tokenStart = loginHtml.IndexOf("__RequestVerificationToken");
			var valueStart = loginHtml.IndexOf("value=\"", tokenStart) + 7;
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
			foreach (var c in cookies)
				loginReq.Headers.Add("Cookie", c.Split(';')[0]);

			var loginResp = await _client.SendAsync(loginReq);
			loginResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

			var authCookie = loginResp.Headers.GetValues("Set-Cookie").FirstOrDefault();
			if (authCookie is not null)
			{
				var req = new HttpRequestMessage(HttpMethod.Get, url);
				req.Headers.Add("Cookie", authCookie.Split(';')[0]);
				return await _client.SendAsync(req);
			}
		}
		return resp;
	}

	static HttpRequestMessage ApiRequest(string path, string apiKey, HttpMethod? method = null)
	{
		var req = new HttpRequestMessage(method ?? HttpMethod.Get, path);
		req.Headers.Add("X-Api-Key", apiKey);
		return req;
	}

	async Task<JsonDocument> PostBindingAsync(string path, string value, string tags)
	{
		tags = $"ws:$system,{tags}";
		var req = ApiRequest("/api/config/$system/bindings", WriteKey, HttpMethod.Post);
		req.Content = JsonContent.Create(new { path, value, tags });
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task CreateBinding_ReturnsOk()
	{
		// Resolution semantics live in ResolvePipelineTests (unit) and ConfV1Tests (/v1/conf);
		// here we just assert the write API accepts a binding.
		var p = $"/test/{Guid.NewGuid():N}";
		using var doc = await PostBindingAsync(p, "v42", "env:dev");
		doc.RootElement.GetProperty("path").GetString().Should().Be(p);
	}

	[Fact]
	public async Task Write_WithoutApiKey_Returns401()
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/api/config/$system/bindings");
		req.Content = JsonContent.Create(new
		{
			path = $"/nokey/{Guid.NewGuid():N}",
			value = "x",
			tags = "env:dev",
		});
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Write_WithReadOnlyKey_Returns403()
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/api/config/$system/bindings");
		req.Headers.Add("X-Api-Key", ReadKey);
		req.Content = JsonContent.Create(new
		{
			path = $"/readonly/{Guid.NewGuid():N}",
			value = "x",
			tags = "env:dev",
		});
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Delete_WithReadOnlyKey_Returns403()
	{
		var req = new HttpRequestMessage(HttpMethod.Delete,
			$"/api/config/$system/bindings?path=/test&tags=env:dev");
		req.Headers.Add("X-Api-Key", ReadKey);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Admin_ProjectsPage_Renders()
	{
		var resp = await GetPageAsync("/ui/admin/projects");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("$system");
	}

	// The project-detail / create-project / shared-config pages moved to workspace-scoped
	// routes during the IA rework; their rendering is covered end-to-end by the Playwright
	// suite (KpVotesOnboardingTests, ConfigPageTests). The old fixed-route HTML smoke tests
	// were deleted rather than re-pointed.
}
