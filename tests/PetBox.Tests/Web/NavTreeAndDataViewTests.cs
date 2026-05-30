using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PetBox.Tests.Web;

// Covers the IA tree lazy-partials (/ui/_nav/tree) and the main-UI data
// leaf-views added in the tree-navigation work: the unified authz gate
// (unknown project/db → 404, unauthenticated → login redirect) and the
// happy-path log listing for the seeded $system project.
[Collection("WebAppFactory")]
public sealed class NavTreeAndDataViewTests : IAsyncLifetime
{
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _client = null!;

	const string TestPassword = "test123";
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public NavTreeAndDataViewTests()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		var dbPath = Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db");
		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = $"Data Source={dbPath};Cache=Shared",
						["Features:Logging"] = "true",
						["Features:Data"] = "true",
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = TestPasswordHash,
					});
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		PetBox.Core.Data.MigrationRunner.Run(cs);
		_client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = _factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<PetBox.Log.Core.Data.ILogStore>();
		if (!await store.ExistsAsync("$system", "default"))
			await store.CreateAsync("$system", "default", null);
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
	}

	// Logs in (anti-forgery + cookie) and returns the authenticated response for url.
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

	[Fact]
	public async Task Tree_Logs_Unauthenticated_RedirectsToLogin()
	{
		using var resp = await _client.GetAsync("/ui/_nav/tree?handler=Logs&project=$system");
		resp.StatusCode.Should().Be(HttpStatusCode.Found);
		resp.Headers.Location!.OriginalString.Should().Contain("/Login");
	}

	[Fact]
	public async Task Tree_Logs_UnknownProject_Returns404()
	{
		using var resp = await GetAuthedAsync("/ui/_nav/tree?handler=Logs&project=does-not-exist");
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Tree_Logs_SystemProject_ListsDefaultLog()
	{
		using var resp = await GetAuthedAsync("/ui/_nav/tree?handler=Logs&project=$system");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("data-log-name=\"default\"");
	}

	[Fact]
	public async Task Tree_Tables_UnknownDb_Returns404()
	{
		using var resp = await GetAuthedAsync("/ui/_nav/tree?handler=Tables&project=$system&db=nope");
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task DatabaseView_UnknownDb_RendersNotFound()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$system/databases/nope");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("database-notfound");
	}
}
