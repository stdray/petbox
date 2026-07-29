using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PetBox.Log.Core.Models;
using PetBox.Log.Core.Query;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Data;

// work/mcp-surface-naming-cleanup wave 3b: log_query used to be the one truncated-carrying verb
// on the surface with no accompanying hint (unlike the search verbs' truncated/omitted/hint
// trio) — the row cap (KqlLimits) named the fact of a cut but never an action. These drive
// LogTools.QueryAsync directly against a scripted ILogQueryService fake so the row-cap trigger
// (KqlLimits.DefaultTake, 1000 rows with no explicit take) doesn't have to be reproduced by
// seeding a real 1000+-row log. Deliberately NO `omitted` assertion anywhere here: a row cap
// does not know how many rows it dropped, so LogQueryResultView carries no such field.
public sealed class LogQueryTruncationHintTests
{
	const string ProjectKey = "proj";

	[Fact]
	public async Task Events_Truncated_CarriesHint()
	{
		var logs = new FakeLogQueryService(new LogQueryResult.Events(Items: [], Truncated: true));
		var result = await LogTools.QueryAsync(Http(), logs, ProjectKey, "default", "events");

		result.Truncated.Should().Be(true);
		result.Hint.Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task Events_NotTruncated_OmitsHint()
	{
		var logs = new FakeLogQueryService(new LogQueryResult.Events(Items: [], Truncated: false));
		var result = await LogTools.QueryAsync(Http(), logs, ProjectKey, "default", "events");

		result.Truncated.Should().BeNull();
		result.Hint.Should().BeNull();
	}

	[Fact]
	public async Task Table_Truncated_CarriesHint()
	{
		var signal = new TruncationSignal { Truncated = true };
		var table = new KqlResult(Columns: [], Rows: EmptyRows());
		var logs = new FakeLogQueryService(new LogQueryResult.Table(table, signal));
		var result = await LogTools.QueryAsync(Http(), logs, ProjectKey, "default", "events | summarize count()");

		result.Truncated.Should().Be(true);
		result.Hint.Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task Table_NotTruncated_OmitsHint()
	{
		var signal = new TruncationSignal { Truncated = false };
		var table = new KqlResult(Columns: [], Rows: EmptyRows());
		var logs = new FakeLogQueryService(new LogQueryResult.Table(table, signal));
		var result = await LogTools.QueryAsync(Http(), logs, ProjectKey, "default", "events | summarize count()");

		result.Truncated.Should().BeNull();
		result.Hint.Should().BeNull();
	}

	static async IAsyncEnumerable<object?[]> EmptyRows()
	{
		await Task.CompletedTask;
		yield break;
	}

	static IHttpContextAccessor Http()
	{
		var id = new ClaimsIdentity([new Claim("project", ProjectKey), new Claim("scopes", "logs:query")], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(id) } };
	}

	sealed class FakeLogQueryService(LogQueryResult result) : ILogQueryService
	{
		public Task<LogQueryResult> QueryAsync(string projectKey, string logName, string kql, CancellationToken ct = default) =>
			Task.FromResult(result);
	}
}
