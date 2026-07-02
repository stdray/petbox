using System.Linq;
using System.Net;
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
// replay byte-for-byte: POST /api/sessions/{project}/{sessionId}?agent=... with an
// application/x-ndjson body — one {role, content} message per line — and X-Api-Key. A shell
// hook can't speak MCP, so this endpoint is the contract — assert the exact wire shape
// (property names/casing) so a rename can't silently break the hook. Plain HttpClient against
// the Kestrel fixture (no browser); keys are seeded straight into PetBoxDb via the fixture's
// service provider.
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
			Key = WriteKey,
			ProjectKey = ProjectKey,
			Scopes = "tasks:read,tasks:write",
			CreatedAt = DateTime.UtcNow,
		});
		await db.InsertAsync(new ApiKey
		{
			Key = ReadOnlyKey,
			ProjectKey = ProjectKey,
			Scopes = "tasks:read",
			CreatedAt = DateTime.UtcNow,
		});
	}

	public Task DisposeAsync()
	{
		_http.Dispose();
		return Task.CompletedTask;
	}

	// Replays exactly what the hook sends: application/x-ndjson body + X-Api-Key.
	static HttpRequestMessage PushRequest(string project, string sessionId, string apiKey, string body, string agent = "opencode")
	{
		var req = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{project}/{sessionId}?agent={agent}");
		req.Headers.Add("X-Api-Key", apiKey);
		req.Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson");
		return req;
	}

	// An ndjson body of {role, content} messages — what the Stop hook now sends.
	static string Ndjson(params (string Role, string Content)[] messages) =>
		string.Join("\n", messages.Select(m => JsonSerializer.Serialize(new { role = m.Role, content = m.Content })));

	[Fact]
	public async Task Push_Then_RePush_AppliesAndAdvancesVersion()
	{
		var sessionId = "s-" + Guid.NewGuid().ToString("N")[..8];

		// 1) First push: 200; echoes sessionId + numeric version (last message ordinal) + messageCount.
		var r1 = await _http.SendAsync(PushRequest(ProjectKey, sessionId, WriteKey, Ndjson(("user", "# plan"))));
		r1.StatusCode.Should().Be(HttpStatusCode.OK);
		using var d1 = JsonDocument.Parse(await r1.Content.ReadAsStringAsync());
		var root1 = d1.RootElement;

		// Case-SENSITIVE property lookup pins the exact wire names/casing.
		root1.TryGetProperty("sessionId", out var sid1).Should().BeTrue("the wire carries `sessionId`");
		sid1.GetString().Should().Be(sessionId);
		root1.TryGetProperty("version", out var ver1).Should().BeTrue("the wire carries `version`");
		ver1.ValueKind.Should().Be(JsonValueKind.Number);
		var version1 = ver1.GetInt64();
		version1.Should().Be(1);
		root1.GetProperty("messageCount").GetInt64().Should().Be(1);

		// 2) Re-push the grown transcript (more messages, last-write-wins) → version advances to the last ordinal.
		var r2 = await _http.SendAsync(PushRequest(ProjectKey, sessionId, WriteKey,
			Ndjson(("user", "# plan"), ("assistant", "step one done"), ("user", "step two"))));
		r2.StatusCode.Should().Be(HttpStatusCode.OK);
		using var d2 = JsonDocument.Parse(await r2.Content.ReadAsStringAsync());
		var root2 = d2.RootElement;
		root2.GetProperty("version").GetInt64().Should().BeGreaterThan(version1);
		root2.GetProperty("version").GetInt64().Should().Be(3);
		root2.GetProperty("messageCount").GetInt64().Should().Be(3);
	}

	// The history importer's upgrade-only guard reads this list and compares its local
	// message count against `version` — pin the wire shape (camelCase names) it parses.
	[Fact]
	public async Task List_ReturnsPushedSessionHeader_WithVersion()
	{
		var sessionId = "s-" + Guid.NewGuid().ToString("N")[..8];
		await _http.SendAsync(PushRequest(ProjectKey, sessionId, WriteKey, Ndjson(("user", "a"), ("assistant", "b"))));

		var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{ProjectKey}");
		req.Headers.Add("X-Api-Key", ReadOnlyKey); // list needs only tasks:read
		var res = await _http.SendAsync(req);

		res.StatusCode.Should().Be(HttpStatusCode.OK);
		using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
		var header = doc.RootElement.GetProperty("sessions").EnumerateArray()
			.Single(s => s.GetProperty("sessionId").GetString() == sessionId);
		header.GetProperty("version").GetInt64().Should().Be(2);
		header.GetProperty("agent").GetString().Should().Be("opencode");
		header.TryGetProperty("updated", out _).Should().BeTrue();
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
		var r = await _http.SendAsync(PushRequest(ProjectKey, sessionId, ReadOnlyKey, Ndjson(("user", "# plan"))));
		r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	// Replays what the append-flow hook sends: the ndjson tail + ?fromOrdinal=N.
	static HttpRequestMessage AppendRequest(string project, string sessionId, string apiKey, long fromOrdinal, string body, string agent = "claude-code")
	{
		var req = new HttpRequestMessage(HttpMethod.Post,
			$"/api/sessions/{project}/{sessionId}/append?agent={agent}&fromOrdinal={fromOrdinal}");
		req.Headers.Add("X-Api-Key", apiKey);
		req.Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson");
		return req;
	}

	// Pins the append wire contract the hooks parse (append.ts): 200 carries `lastOrdinal` +
	// `appended`; overlap is idempotent; a gap is a STRUCTURED 409 with `lastOrdinal` inside.
	[Fact]
	public async Task Append_Contiguous_Overlap_Gap_WireContract()
	{
		var sessionId = "s-" + Guid.NewGuid().ToString("N")[..8];

		// 1) New session, fromOrdinal=1 → 200 { sessionId, lastOrdinal, appended }.
		var r1 = await _http.SendAsync(AppendRequest(ProjectKey, sessionId, WriteKey, 1, Ndjson(("user", "q"), ("assistant", "a"))));
		r1.StatusCode.Should().Be(HttpStatusCode.OK);
		using var d1 = JsonDocument.Parse(await r1.Content.ReadAsStringAsync());
		d1.RootElement.GetProperty("sessionId").GetString().Should().Be(sessionId);
		d1.RootElement.TryGetProperty("lastOrdinal", out var lo1).Should().BeTrue("the wire carries `lastOrdinal`");
		lo1.GetInt64().Should().Be(2);
		d1.RootElement.GetProperty("appended").GetInt64().Should().Be(2);

		// 2) Overlapping re-send of the same ordinals + a new tail → idempotent, only the tail lands.
		var r2 = await _http.SendAsync(AppendRequest(ProjectKey, sessionId, WriteKey, 1, Ndjson(("user", "q"), ("assistant", "a"), ("user", "q2"))));
		r2.StatusCode.Should().Be(HttpStatusCode.OK);
		using var d2 = JsonDocument.Parse(await r2.Content.ReadAsStringAsync());
		d2.RootElement.GetProperty("lastOrdinal").GetInt64().Should().Be(3);
		d2.RootElement.GetProperty("appended").GetInt64().Should().Be(1);

		// 3) Gap → 409 with the structured body the hook self-heals from.
		var r3 = await _http.SendAsync(AppendRequest(ProjectKey, sessionId, WriteKey, 9, Ndjson(("user", "late"))));
		r3.StatusCode.Should().Be(HttpStatusCode.Conflict);
		using var d3 = JsonDocument.Parse(await r3.Content.ReadAsStringAsync());
		d3.RootElement.GetProperty("error").GetString().Should().Be("gap");
		d3.RootElement.TryGetProperty("lastOrdinal", out var lo3).Should().BeTrue("the 409 body carries the server cursor");
		lo3.GetInt64().Should().Be(3);
	}

	[Fact]
	public async Task Append_MissingFromOrdinal_400()
	{
		var sessionId = "s-" + Guid.NewGuid().ToString("N")[..8];
		var req = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{ProjectKey}/{sessionId}/append?agent=claude-code");
		req.Headers.Add("X-Api-Key", WriteKey);
		req.Content = new StringContent(Ndjson(("user", "q")), Encoding.UTF8, "application/x-ndjson");
		var r = await _http.SendAsync(req);
		r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Append_MissingTasksWriteScope_403()
	{
		var sessionId = "s-" + Guid.NewGuid().ToString("N")[..8];
		var r = await _http.SendAsync(AppendRequest(ProjectKey, sessionId, ReadOnlyKey, 1, Ndjson(("user", "q"))));
		r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	static HttpRequestMessage DeleteRequest(string project, string sessionId, string apiKey)
	{
		var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/sessions/{project}/{sessionId}");
		req.Headers.Add("X-Api-Key", apiKey);
		return req;
	}

	[Fact]
	public async Task Delete_Then_404OnRepeat_Then_RePushResurrects()
	{
		var sessionId = "s-" + Guid.NewGuid().ToString("N")[..8];
		await _http.SendAsync(PushRequest(ProjectKey, sessionId, WriteKey, Ndjson(("user", "# plan"))));

		// 1) Delete: 200 with the exact wire shape { deleted: true }.
		var r1 = await _http.SendAsync(DeleteRequest(ProjectKey, sessionId, WriteKey));
		r1.StatusCode.Should().Be(HttpStatusCode.OK);
		using var d1 = JsonDocument.Parse(await r1.Content.ReadAsStringAsync());
		d1.RootElement.TryGetProperty("deleted", out var del).Should().BeTrue("the wire carries `deleted`");
		del.GetBoolean().Should().BeTrue();

		// 2) Repeat delete: idempotent miss → 404 with an error body.
		var r2 = await _http.SendAsync(DeleteRequest(ProjectKey, sessionId, WriteKey));
		r2.StatusCode.Should().Be(HttpStatusCode.NotFound);

		// 3) Re-push the same sessionId: resurrects (the hook re-pushes a live session every turn).
		var r3 = await _http.SendAsync(PushRequest(ProjectKey, sessionId, WriteKey, Ndjson(("user", "# plan"), ("assistant", "back"))));
		r3.StatusCode.Should().Be(HttpStatusCode.OK);
		using var d3 = JsonDocument.Parse(await r3.Content.ReadAsStringAsync());
		d3.RootElement.GetProperty("version").GetInt64().Should().Be(2);

		var r4 = await _http.SendAsync(DeleteRequest(ProjectKey, sessionId, WriteKey)); // visible again
		r4.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Delete_MissingTasksWriteScope_403()
	{
		var sessionId = "s-" + Guid.NewGuid().ToString("N")[..8];
		await _http.SendAsync(PushRequest(ProjectKey, sessionId, WriteKey, Ndjson(("user", "# plan"))));

		var r = await _http.SendAsync(DeleteRequest(ProjectKey, sessionId, ReadOnlyKey));
		r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}
}
