using System.Net;
using System.Text;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Data;

namespace PetBox.Tests.Web;

// authz-cleanup-phase2-rest: LogApi.IngestClefPathAsync (POST /api/ingest/{projectKey}/{logName}/clef)
// carried ONLY the "ApiKey" policy (proves some api key authenticated) — unlike every other handler
// in LogApi.cs (CreateLogAsync/ListLogsAsync/DeleteLogAsync/QueryLogsAsync/GetServicesAsync/
// LiveTailAsync), it was missing the AuthorizeProject/logs:ingest check, so any api key could ingest
// CLEF events into ANY project's named log via the path-based route. Fixed by adding the identical
// AuthorizeProject + HasScope(LogsIngest) check the sibling handlers already perform. Reuses
// LogPipelineFixture (shared host + $system/default log already created) and its
// SeedProjectKeyAsync-style project/key seeding, mirroring LogPipelineTests' own
// CompatSeq_ForeignProjectKey_Returns403 / LogQuery_ForeignProject_Returns403 cross-project tests
// (which already exist for the OTHER LogApi handlers — this endpoint had no equivalent).
[Collection(LogPipelineCollectionDef.Name)]
public sealed class LogIngestClefAuthzTests
{
	readonly LogPipelineFixture _fx;
	readonly HttpClient _client;

	public LogIngestClefAuthzTests(LogPipelineFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	async Task SeedProjectKeyAsync(string apiKey, string projectKey, string scopes, bool createDefaultLog)
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		await db.InsertAsync(new Project { Key = projectKey, WorkspaceKey = LogNames.SystemProject, Name = projectKey });
		await db.InsertAsync(new ApiKey { Key = apiKey, ProjectKey = projectKey, Scopes = scopes, Name = apiKey, CreatedAt = DateTime.UtcNow });
		if (createDefaultLog)
		{
			var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
			if (!await store.ExistsAsync(projectKey, LogNames.Default))
				await store.CreateAsync(projectKey, LogNames.Default, null);
		}
	}

	int ProjectLogCount(string projectKey, string logName, string message)
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		var ctx = store.GetContext(projectKey, logName);
		return ctx.LogEntries.Count(e => e.Message == message);
	}

	static string UniqueMsg(string marker) => $"__ingest_authz__{marker}__{Guid.NewGuid():N}";

	static HttpRequestMessage ClefReq(string apiKey, string projectKey, string logName, string svc, string jsonl)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, $"/api/ingest/{projectKey}/{logName}/clef");
		req.Headers.Add("X-Api-Key", apiKey);
		req.Headers.Add("X-Service-Key", svc);
		req.Content = new StringContent(jsonl, Encoding.UTF8, "text/plain");
		return req;
	}

	[Fact]
	public async Task Ingest_OwnProject_WithScope_Succeeds()
	{
		var proj = $"clefauthz{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:ingest", createDefaultLog: true);

		var msg = UniqueMsg("own");
		using var resp = await _client.SendAsync(ClefReq(key, proj, LogNames.Default, "svc",
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{msg}}"}"""));
		resp.StatusCode.Should().Be(HttpStatusCode.OK,
			"a logs:ingest key must be able to ingest into its OWN project's named log");
	}

	[Fact]
	public async Task Ingest_ForeignProject_Returns403_AndDoesNotLand()
	{
		var proj = $"clefauthz{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:ingest", createDefaultLog: true);

		// proj's key must not be able to write into the foreign $system/default log.
		var msg = UniqueMsg("foreign");
		using var resp = await _client.SendAsync(ClefReq(key, LogNames.SystemProject, LogNames.Default, "svc",
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{msg}}"}"""));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"a key authorized only for its own project must not ingest CLEF events into a foreign project's log");

		// Give any (incorrectly) enqueued write a moment to land, then assert it never did.
		await Task.Delay(200);
		ProjectLogCount(LogNames.SystemProject, LogNames.Default, msg).Should().Be(0,
			"the cross-project ingest must not have landed any row in the foreign log");
	}

	[Fact]
	public async Task Ingest_OwnProject_WithoutIngestScope_Returns403()
	{
		var proj = $"clefauthz{Guid.NewGuid():N}"[..16];
		var key = $"yb_key_{Guid.NewGuid():N}";
		await SeedProjectKeyAsync(key, proj, "logs:query", createDefaultLog: true);

		using var resp = await _client.SendAsync(ClefReq(key, proj, LogNames.Default, "svc",
			$$"""{"@t":"2024-01-01T00:00:00Z","@l":"Info","@m":"{{UniqueMsg("noscope")}}"}"""));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"a project-authorized key WITHOUT logs:ingest must still be denied");
	}
}
