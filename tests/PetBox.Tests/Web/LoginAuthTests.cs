using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// WS1 (persistent cookie) + WS3 (bootstrap-admin lockdown) — both exercised via the login POST.
[Collection("WebAppFactory")]
public sealed class LoginAuthTests : IAsyncLifetime
{
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _client = null!;

	const string Password = "test123";
	const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public LoginAuthTests()
	{
		Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__PETBOX", $"Data Source={Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db")};Cache=Shared");
		Environment.SetEnvironmentVariable("PETBOX_ADMIN_FORCE", null);
		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = PasswordHash,
					});
				});
			});
	}

	public Task InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		// HandleCookies=false → stateless requests; we pass the antiforgery cookie manually and
		// never leak an auth cookie between logins (each GET /Login re-issues the antiforgery cookie).
		_client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
		Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__PETBOX", null);
	}

	async Task<HttpResponseMessage> LoginAsync(string username, string password)
	{
		var page = await _client.GetAsync("/Login");
		var html = await page.Content.ReadAsStringAsync();
		var tokenStart = html.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = html.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var valueEnd = html.IndexOf('"', valueStart);
		var token = html[valueStart..valueEnd];
		var cookie = page.Headers.GetValues("Set-Cookie").First().Split(';')[0];

		var req = new HttpRequestMessage(HttpMethod.Post, "/Login");
		req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = username,
			["password"] = password,
			["__RequestVerificationToken"] = token,
		});
		req.Headers.Add("Cookie", cookie);
		return await _client.SendAsync(req);
	}

	async Task SeedSysAdminAsync(string username)
	{
		using var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		var id = await db.InsertWithInt64IdentityAsync(new User { Username = username, PasswordHash = PasswordHash, CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new WorkspaceMember { UserId = id, WorkspaceKey = "$system", Role = WorkspaceRole.Admin });
	}

	[Fact]
	public async Task Login_IssuesPersistentCookie()
	{
		using var resp = await LoginAsync("admin", Password);
		resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var authCookie = resp.Headers.GetValues("Set-Cookie")
			.First(c => c.StartsWith(".AspNetCore.Cookies", StringComparison.OrdinalIgnoreCase));
		// Persistent cookies carry an expires/max-age attribute; session cookies do not.
		authCookie.Should().MatchRegex("(?i)expires=|max-age=");
	}

	[Fact]
	public async Task BootstrapAdmin_Disabled_OnceAnotherSysAdminExists()
	{
		// First boot: env-admin works.
		using (var ok = await LoginAsync("admin", Password))
			ok.StatusCode.Should().Be(HttpStatusCode.Redirect);

		await SeedSysAdminAsync("owner");

		// Now the bootstrap admin is locked out, but the real admin still logs in.
		using var locked = await LoginAsync("admin", Password);
		locked.StatusCode.Should().Be(HttpStatusCode.OK); // re-renders page with error, no redirect

		using var owner = await LoginAsync("owner", Password);
		owner.StatusCode.Should().Be(HttpStatusCode.Redirect);
	}

	[Fact]
	public async Task BootstrapAdmin_Force_Reenables()
	{
		await SeedSysAdminAsync("owner");
		Environment.SetEnvironmentVariable("PETBOX_ADMIN_FORCE", "true");
		try
		{
			using var resp = await LoginAsync("admin", Password);
			resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		}
		finally
		{
			Environment.SetEnvironmentVariable("PETBOX_ADMIN_FORCE", null);
		}
	}
}
