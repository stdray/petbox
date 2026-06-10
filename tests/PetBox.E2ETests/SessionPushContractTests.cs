using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.E2ETests.Infrastructure;

namespace PetBox.E2ETests;

// Pins the REST session-push contract that the agent Stop hooks (opencode / claude-code)
// replay byte-for-byte: POST /api/sessions/{project}/{sessionId}?agent=... with a
// text/plain markdown body and X-Api-Key. A shell hook can't speak MCP, so this endpoint
// is the contract — assert the exact wire shape (property names/casing) so a rename can't
// silently break the hook. Plain HttpClient against the Kestrel fixture (no browser);
// keys are seeded straight into PetBoxDb via the fixture's service provider.
[Collection(nameof(UiCollection))]
public sealed class SessionPushContractTests(WebAppFixture app) : IAsyncLifetime
{
	const string Workspace = "sessctr-ws";
	const string ProjectKey = "sessctr";
	const string WriteKey = "yb_key_sessctr_write";   // has tasks:write
	const string ReadOnlyKey = "yb_key_sessctr_read"; // lacks tasks:write

	HttpClient _http = null!;

	public async Task InitializeAsync()
	{
		_http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };

		using var scope = app.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		await db.ApiKeys.Where(k => k.Key == WriteKey || k.Key == ReadOnlyKey).DeleteAsync();
		await db.Projects.Where(p => p.Key == ProjectKey).DeleteAsync();
		await db.Workspaces.Where(w => w.Key == Workspace).DeleteAsync();

		await db.InsertAsync(new Workspace { Key = Workspace, Name = "SessCtr", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = ProjectKey, WorkspaceKey = Workspace, Name = "SessCtr" });
		await db.InsertAsync(new ApiKey
		{
			Key = WriteKey, ProjectKey = ProjectKey, Scopes = "tasks:read,tasks:write", CreatedAt = DateTime.UtcNow,
		});
		await db.InsertAsync(new ApiKey
		{
			Key = ReadOnlyKey, ProjectKey = ProjectKey, Scopes = "tasks:read", CreatedAt = DateTime.UtcNow,
		});
	}

	public Task DisposeAsync()
	{
		_http.Dispose();
		return Task.CompletedTask;
	}

	// Replays exactly what the hook sends: text/plain; charset=utf-8 raw markdown body + X-Api-Key.
	static HttpRequestMessage PushRequest(string project, string sessionId, string apiKey, string body, string agent = "opencode")
	{
		var req = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{project}/{sessionId}?agent={agent}");
		req.Headers.Add("X-Api-Key", apiKey);
		req.Content = new StringContent(body, Encoding.UTF8, "text/plain");
		return req;
	}

	[Fact]
	public async Task Push_Then_RePush_AppliesAndAdvancesVersion()
	{
		var sessionId = "s-" + Guid.NewGuid().ToString("N")[..8];

		// 1) First push: 200, applied == true (boolean), numeric currentVersion.
		var r1 = await _http.SendAsync(PushRequest(ProjectKey, sessionId, WriteKey, "# plan\n\n- step one\n"));
		r1.StatusCode.Should().Be(HttpStatusCode.OK);
		using var d1 = JsonDocument.Parse(await r1.Content.ReadAsStringAsync());
		var root1 = d1.RootElement;

		// Case-SENSITIVE property lookup pins the exact wire names/casing.
		root1.TryGetProperty("applied", out var applied1).Should().BeTrue("the hook reads `applied`");
		applied1.ValueKind.Should().Be(JsonValueKind.True);
		root1.TryGetProperty("currentVersion", out var ver1).Should().BeTrue("the hook reads `currentVersion`");
		ver1.ValueKind.Should().Be(JsonValueKind.Number);
		var version1 = ver1.GetInt64();

		// 2) Second push (last-write-wins, no conflict): 200, applied true, strictly greater version.
		var r2 = await _http.SendAsync(PushRequest(ProjectKey, sessionId, WriteKey, "# plan v2\n\n- step one done\n- step two\n"));
		r2.StatusCode.Should().Be(HttpStatusCode.OK);
		using var d2 = JsonDocument.Parse(await r2.Content.ReadAsStringAsync());
		var root2 = d2.RootElement;
		root2.GetProperty("applied").GetBoolean().Should().BeTrue();
		root2.GetProperty("currentVersion").GetInt64().Should().BeGreaterThan(version1);
	}

	[Fact]
	public async Task Push_EmptyBody_400WithErrorMessage()
	{
		var sessionId = "s-" + Guid.NewGuid().ToString("N")[..8];
		var r = await _http.SendAsync(PushRequest(ProjectKey, sessionId, WriteKey, ""));
		r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
		d.RootElement.TryGetProperty("error", out var err).Should().BeTrue("the body carries an `error` property");
		err.GetString().Should().Be("empty body");
	}

	[Fact]
	public async Task Push_MissingTasksWriteScope_403()
	{
		var sessionId = "s-" + Guid.NewGuid().ToString("N")[..8];
		var r = await _http.SendAsync(PushRequest(ProjectKey, sessionId, ReadOnlyKey, "# plan\n"));
		r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}
}
