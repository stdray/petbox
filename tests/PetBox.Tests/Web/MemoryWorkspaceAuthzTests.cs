using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// workspace-memory-isolation: Memory pages use WorkspaceViewer — a member of wsa must not
// read shared memory of wsb via /ui/wsb/$ws-wsb/memory (membership IDOR, not just field match).
public sealed class MemoryWorkspaceAuthzFixture : IAsyncLifetime
{
	public const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Password = "test123";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public MemoryWorkspaceAuthzFixture()
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
						["Features:Memory"] = "true",
					});
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
			HandleCookies = false,
		});

		using var scope = Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();

		await db.InsertAsync(new Workspace { Key = "wsa", Name = "Wsa", Description = "", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Workspace { Key = "wsb", Name = "Wsb", Description = "", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = "proja", WorkspaceKey = "wsa", Name = "A", Description = "" });
		// Shared-memory containers for both workspaces (as if MCP/UI had ensured them).
		await db.InsertAsync(new Project { Key = "$ws-wsa", WorkspaceKey = "wsa", Name = "Workspace", Description = "" });
		await db.InsertAsync(new Project { Key = "$ws-wsb", WorkspaceKey = "wsb", Name = "Workspace", Description = "" });

		// eve: Member of wsa only — not wsb.
		var eveId = await db.InsertWithInt64IdentityAsync(new User
		{
			Username = "eve-mem", PasswordHash = PasswordHash, CreatedAt = DateTime.UtcNow,
		});
		await db.InsertAsync(new WorkspaceMember
		{
			UserId = eveId, WorkspaceKey = "wsa", Role = WorkspaceRole.Member,
		});
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class MemoryWorkspaceAuthzTests : IClassFixture<MemoryWorkspaceAuthzFixture>
{
	readonly MemoryWorkspaceAuthzFixture _fx;
	readonly HttpClient _client;

	public MemoryWorkspaceAuthzTests(MemoryWorkspaceAuthzFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	static (string Token, string Cookie) ExtractAntiforgery(HttpResponseMessage resp, string html)
	{
		var tokenStart = html.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = html.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var valueEnd = html.IndexOf('"', valueStart);
		var token = html[valueStart..valueEnd];
		var cookie = resp.Headers.GetValues("Set-Cookie")
			.First(c => c.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase))
			.Split(';')[0];
		return (token, cookie);
	}

	async Task<string> LoginAsync(string username)
	{
		var loginPage = await _client.GetAsync("/Login");
		var loginHtml = await loginPage.Content.ReadAsStringAsync();
		var (token, afCookie) = ExtractAntiforgery(loginPage, loginHtml);

		var req = new HttpRequestMessage(HttpMethod.Post, "/Login");
		req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = username,
			["password"] = MemoryWorkspaceAuthzFixture.Password,
			["__RequestVerificationToken"] = token,
		});
		req.Headers.Add("Cookie", afCookie);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, $"login as '{username}' must succeed");
		return resp.Headers.GetValues("Set-Cookie")
			.First(c => c.StartsWith(".AspNetCore.Cookies", StringComparison.OrdinalIgnoreCase))
			.Split(';')[0];
	}

	[Fact]
	public async Task Member_of_wsa_can_open_own_shared_memory()
	{
		var auth = await LoginAsync("eve-mem");
		var req = new HttpRequestMessage(HttpMethod.Get, "/ui/wsa/$ws-wsa/memory");
		req.Headers.Add("Cookie", auth);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("memory-title");
		html.Should().NotContain("memory-notfound");
	}

	[Fact]
	public async Task Member_of_wsa_cannot_open_wsb_shared_memory()
	{
		var auth = await LoginAsync("eve-mem");
		var req = new HttpRequestMessage(HttpMethod.Get, "/ui/wsb/$ws-wsb/memory");
		req.Headers.Add("Cookie", auth);
		using var resp = await _client.SendAsync(req);
		// WorkspaceViewer denies → challenge redirect to Login (not 200 with body).
		resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		resp.Headers.Location!.ToString().Should().Contain("/Login");
	}
}
