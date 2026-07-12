using System.Net;
using System.Text;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// Regression for the real incident (spec work/smoke-writes-into-real-projects): smoke background
// jobs' session pushes landed in $system / yobapub. SessionApi.UpsertAsync (POST
// /api/sessions/{projectKey}/{sessionId}) is the Stop-hook's non-MCP write path — exactly the route
// a smoke session travels — and it now goes through ProjectScope.AuthorizesAsync instead of the old
// claim-only Authorizes, so a sandboxOnly key is refused on any project that isn't flagged Sandbox.
public sealed class SessionApiSandboxAuthzFixture : IAsyncLifetime
{
	public const string RealProject = "sessandboxreal";
	public const string SandboxProject = "sessandboxsandbox";
	public const string SandboxOnlyKey = "yb_key_sessbx_only";
	public const string WildcardSandboxOnlyKey = "yb_key_sessbx_wild";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public SessionApiSandboxAuthzFixture()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
				["Host:BackgroundServices"] = "false",
				["Features:Tasks"] = "true",
			}));
		});
	}

	public async Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		await db.InsertAsync(new Project { Key = RealProject, WorkspaceKey = "$system", Name = "Real", Sandbox = false });
		await db.InsertAsync(new Project { Key = SandboxProject, WorkspaceKey = "$system", Name = "Sandbox", Sandbox = true });
		await db.InsertAsync(new ApiKey
		{
			Key = SandboxOnlyKey, ProjectKey = SandboxProject, Scopes = "tasks:read,tasks:write",
			SandboxOnly = true, CreatedAt = DateTime.UtcNow,
		});
		await db.InsertAsync(new ApiKey
		{
			Key = WildcardSandboxOnlyKey, ProjectKey = ProjectScope.AllProjects, Scopes = "tasks:read,tasks:write",
			SandboxOnly = true, CreatedAt = DateTime.UtcNow,
		});
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class SessionApiSandboxAuthzTests : IClassFixture<SessionApiSandboxAuthzFixture>
{
	readonly SessionApiSandboxAuthzFixture _fx;
	readonly HttpClient _client;

	public SessionApiSandboxAuthzTests(SessionApiSandboxAuthzFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	static HttpRequestMessage UpsertReq(string apiKey, string projectKey, string sessionId)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{projectKey}/{sessionId}");
		req.Headers.Add("X-Api-Key", apiKey);
		req.Content = new StringContent(
			"""{"role":"user","content":"sandbox gate regression probe"}""" + "\n",
			Encoding.UTF8, "application/x-ndjson");
		return req;
	}

	// THE incident this reproduces: a sandboxOnly key's session push must be REFUSED against a
	// real (non-sandbox) project — 403, not a silent write into $system/yobapub.
	[Fact]
	public async Task Upsert_SandboxOnlyKey_IntoARealProject_Returns403()
	{
		using var resp = await _client.SendAsync(
			UpsertReq(SessionApiSandboxAuthzFixture.SandboxOnlyKey, SessionApiSandboxAuthzFixture.RealProject, "reg-scoped-bad"));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	// The wildcard case: identity authorizes the real project, containment must still refuse it.
	[Fact]
	public async Task Upsert_WildcardSandboxOnlyKey_IntoARealProject_Returns403()
	{
		using var resp = await _client.SendAsync(
			UpsertReq(SessionApiSandboxAuthzFixture.WildcardSandboxOnlyKey, SessionApiSandboxAuthzFixture.RealProject, "reg-wild-bad"));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"the wildcard claim authorizes every project by identity — the sandbox containment check must still refuse a real one");
	}

	// …and the SAME sandboxOnly key succeeds against the sandbox project it is actually meant for.
	[Fact]
	public async Task Upsert_SandboxOnlyKey_IntoItsSandboxProject_Succeeds()
	{
		using var resp = await _client.SendAsync(
			UpsertReq(SessionApiSandboxAuthzFixture.SandboxOnlyKey, SessionApiSandboxAuthzFixture.SandboxProject, "reg-scoped-ok"));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
	}
}
