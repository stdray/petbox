using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Tests.Web;

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
		Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__YOBOBOX", "Data Source=:memory:;Cache=Shared");
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
		_client = _factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
		});

		using var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<YobaBoxDb>();
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
		Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__YOBOBOX", null);
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

	async Task<JsonDocument?> ResolveAsync(string path, string tags)
	{
		var req = ApiRequest($"/api/config/$system/resolve?path={Uri.EscapeDataString(path)}&tags={Uri.EscapeDataString(tags)}", ReadKey);
		using var resp = await _client.SendAsync(req);
		if (resp.StatusCode == HttpStatusCode.NotFound)
			return null;
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
	}

	async Task DeleteBindingAsync(string path, string tags)
	{
		tags = $"ws:$system,{tags}";
		var req = ApiRequest(
			$"/api/config/$system/bindings?path={Uri.EscapeDataString(path)}&tags={Uri.EscapeDataString(tags)}",
			WriteKey,
			HttpMethod.Delete);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task CreateAndResolve_SingleBinding()
	{
		var p = $"/test/{Guid.NewGuid():N}";
		await PostBindingAsync(p, "v42", "env:dev");

		var doc = await ResolveAsync(p, "env:dev");
		doc.Should().NotBeNull();
		doc!.RootElement.GetProperty("value").GetString().Should().Be("v42");
	}

	[Fact]
	public async Task Resolve_NoMatch_Returns404()
	{
		var req = ApiRequest($"/api/config/$system/resolve?path=/nonexistent/{Guid.NewGuid():N}&tags=env:dev", ReadKey);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Resolve_MostSpecificTagWins()
	{
		var p = $"/override/{Guid.NewGuid():N}";

		await PostBindingAsync(p, "generic", "env:dev");
		await PostBindingAsync(p, "specific", "env:dev,service:alpha");

		var doc = await ResolveAsync(p, "env:dev");
		doc.Should().NotBeNull();
		doc!.RootElement.GetProperty("value").GetString().Should().Be("generic");

		var doc2 = await ResolveAsync(p, "env:dev,service:alpha");
		doc2.Should().NotBeNull();
		doc2!.RootElement.GetProperty("value").GetString().Should().Be("specific");
	}

	[Fact]
	public async Task Delete_Binding_ThenNotFound()
	{
		var p = $"/del/{Guid.NewGuid():N}";
		var tag = $"marker:{Guid.NewGuid():N}";

		await PostBindingAsync(p, "gone", tag);
		await DeleteBindingAsync(p, tag);

		var doc = await ResolveAsync(p, tag);
		doc.Should().BeNull();
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
	public async Task Read_WithoutApiKey_Returns401()
	{
		using var resp = await _client.GetAsync($"/api/config/$system/resolve?path=/test&tags=env:dev");
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
	public async Task Read_WithWriteKey_Returns200()
	{
		var p = $"/readwrite/{Guid.NewGuid():N}";
		await PostBindingAsync(p, "v1", "env:dev");

		var req = ApiRequest(
			$"/api/config/$system/resolve?path={Uri.EscapeDataString(p)}&tags=env:dev", WriteKey);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Admin_ProjectsPage_Renders()
	{
		var resp = await GetPageAsync("/ui/admin/projects");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("$system");
	}

	[Fact]
	public async Task Admin_ProjectDetail_Renders()
	{
		var resp = await GetPageAsync("/ui/admin/projects/$system");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("$system");
	}

	[Fact]
	public async Task Admin_CreateProject_Form_Renders()
	{
		var resp = await GetPageAsync("/ui/admin/projects?handler=Create");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("admin-project-create-form");
		html.Should().Contain("admin-project-create-key");
	}

	[Fact]
	public async Task Admin_CreateProject_Then_Detail_Renders()
	{
		var projectKey = "test-" + Guid.NewGuid().ToString("N")[..8];

		// Login to get auth cookie
		var loginPage = await _client.GetAsync("/Login");
		var loginHtml = await loginPage.Content.ReadAsStringAsync();
		var tokenStart = loginHtml.IndexOf("__RequestVerificationToken");
		var valueStart = loginHtml.IndexOf("value=\"", tokenStart) + 7;
		var valueEnd = loginHtml.IndexOf('"', valueStart);
		var loginToken = loginHtml[valueStart..valueEnd];
		var loginCookie = string.Join("; ", loginPage.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

		var loginReq = new HttpRequestMessage(HttpMethod.Post, "/Login");
		loginReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = "admin",
			["password"] = TestPassword,
			["__RequestVerificationToken"] = loginToken,
		});
		loginReq.Headers.Add("Cookie", loginCookie);

		var loginResp = await _client.SendAsync(loginReq);
		loginResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var authCookie = string.Join("; ", loginResp.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

		// GET create form (now embedded in the main projects page)
		var formReq = new HttpRequestMessage(HttpMethod.Get, "/ui/admin/projects");
		formReq.Headers.Add("Cookie", authCookie);
		var formResp = await _client.SendAsync(formReq);
		formResp.StatusCode.Should().Be(HttpStatusCode.OK);
		var formHtml = await formResp.Content.ReadAsStringAsync();
		var afTokenStart = formHtml.IndexOf("__RequestVerificationToken");
		var afValueStart = formHtml.IndexOf("value=\"", afTokenStart) + 7;
		var afValueEnd = formHtml.IndexOf('"', afValueStart);
		var afToken = formHtml[afValueStart..afValueEnd];

		// POST to create
		var createReq = new HttpRequestMessage(HttpMethod.Post, "/ui/admin/projects?handler=Create");
		createReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["Key"] = projectKey,
			["WorkspaceKey"] = "$system",
			["Name"] = "Test Project",
			["Description"] = "A test",
			["__RequestVerificationToken"] = afToken,
		});
		createReq.Headers.Add("Cookie", authCookie);

		var createResp = await _client.SendAsync(createReq);
		createResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

		// Verify project detail page renders
		var detailReq = new HttpRequestMessage(HttpMethod.Get, "/ui/admin/projects/" + projectKey);
		detailReq.Headers.Add("Cookie", authCookie);
		var detailResp = await _client.SendAsync(detailReq);
		detailResp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await detailResp.Content.ReadAsStringAsync();
		html.Should().Contain("Test Project");
	}

	[Fact]
	public async Task ConfigPage_Renders()
	{
		var resp = await GetPageAsync("/ui/config");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("Config");
	}
}
