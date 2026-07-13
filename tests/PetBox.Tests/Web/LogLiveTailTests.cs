using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Ingestion;
using PetBox.Log.Core.Models;
using PetBox.Log.Core.Query;

namespace PetBox.Tests.Web;

// live-tail-sse-transport-broken. The live tail shipped and delivered NOTHING, and it went unnoticed
// because no test ever drove the transport end to end: /api/logs/{p}/{log}/live-tail was gated by the
// header-only "ApiKey" policy, while the only client that opens it is a browser EventSource — which
// cannot send headers and brings a cookie. Everything below is the path a browser actually takes.
//
// This suite is where the transport lives, NOT the Playwright suite: an SSE assertion in a browser is
// a race between the EventSource handshake, the ingestion pipeline's writer loop and the test's own
// clock — flaky by construction. The browser side keeps the one thing only a browser can prove (the
// bundled htmx SSE extension registers and actually opens the EventSource — LogsPageTests), and every
// SEMANTIC claim (who may tail what, what a subscriber is owed) is pinned here, where the stream is a
// plain HTTP response and delivery is deterministic.
public sealed class LiveTailFixture : IAsyncLifetime
{
	public const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Password = "test123";

	public const string WsA = "tailwsa";
	public const string WsB = "tailwsb";
	public const string ProjA = "tailproja";
	public const string ProjB = "tailprojb";
	public const string Log = "default";

	// A key scoped to ProjA WITH logs:query — the api-key path must keep working exactly as before.
	public const string KeyQuery = "yb_key_tail_query";
	// Same project, but no logs:query — the scope gate must still refuse it (the cookie door must not
	// have become a way around the scope an api key lacks).
	public const string KeyNoScope = "yb_key_tail_noscope";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public LiveTailFixture()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Features:Logging"] = "true",
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = PasswordHash,
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
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();

		await db.InsertAsync(new Workspace { Key = WsA, Name = "A", Description = "", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Workspace { Key = WsB, Name = "B", Description = "", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = ProjA, WorkspaceKey = WsA, Name = "A", Description = "" });
		await db.InsertAsync(new Project { Key = ProjB, WorkspaceKey = WsB, Name = "B", Description = "" });

		// The browser session under test: a Member of wsa, and of nothing else.
		var memberId = await db.InsertWithInt64IdentityAsync(new User
		{
			Username = "tail-member",
			PasswordHash = PasswordHash,
			CreatedAt = DateTime.UtcNow,
		});
		await db.SeedMemberAsync(memberId, WsA, WorkspaceRole.Member);

		await db.InsertAsync(new ApiKey
		{
			Key = KeyQuery,
			ProjectKey = ProjA,
			Scopes = "logs:query,logs:ingest",
			Name = KeyQuery,
			CreatedAt = DateTime.UtcNow,
		});
		await db.InsertAsync(new ApiKey
		{
			Key = KeyNoScope,
			ProjectKey = ProjA,
			Scopes = "logs:ingest",
			Name = KeyNoScope,
			CreatedAt = DateTime.UtcNow,
		});

		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		foreach (var project in new[] { ProjA, ProjB })
		{
			if (!await store.ExistsAsync(project, Log))
				await store.CreateAsync(project, Log, null);
		}
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class LogLiveTailTests : IClassFixture<LiveTailFixture>
{
	readonly LiveTailFixture _fx;
	readonly HttpClient _client;

	public LogLiveTailTests(LiveTailFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	static string Url(string project, string? since = null, string? kql = null)
	{
		var query = new List<string>();
		if (since is not null) query.Add($"since={Uri.EscapeDataString(since)}");
		if (kql is not null) query.Add($"kql={Uri.EscapeDataString(kql)}");
		return $"/api/logs/{project}/{LiveTailFixture.Log}/live-tail" + (query.Count == 0 ? "" : "?" + string.Join('&', query));
	}

	// ---- the browser's half: a real cookie session, obtained the way a browser obtains one ----

	static (string Token, string Cookie) ExtractAntiforgery(HttpResponseMessage resp, string html)
	{
		var tokenStart = html.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = html.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var valueEnd = html.IndexOf('"', valueStart);
		var cookie = resp.Headers.GetValues("Set-Cookie")
			.First(c => c.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase))
			.Split(';')[0];
		return (html[valueStart..valueEnd], cookie);
	}

	async Task<string> LoginAsync(string username)
	{
		using var loginPage = await _client.GetAsync("/Login");
		var (token, afCookie) = ExtractAntiforgery(loginPage, await loginPage.Content.ReadAsStringAsync());

		using var req = new HttpRequestMessage(HttpMethod.Post, "/Login");
		req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = username,
			["password"] = LiveTailFixture.Password,
			["__RequestVerificationToken"] = token,
		});
		req.Headers.Add("Cookie", afCookie);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, $"login as '{username}' must succeed");
		return resp.Headers.GetValues("Set-Cookie")
			.First(c => c.StartsWith(".AspNetCore.Cookies", StringComparison.OrdinalIgnoreCase))
			.Split(';')[0];
	}

	// ---- opening the stream, and reading rows off it ----

	sealed record Tail(HttpResponseMessage Response, StreamReader Reader) : IDisposable
	{
		public void Dispose()
		{
			Reader.Dispose();
			Response.Dispose();
		}
	}

	async Task<HttpResponseMessage> OpenRawAsync(string url, string? cookie, string? apiKey, CancellationToken ct)
	{
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		if (cookie is not null) req.Headers.Add("Cookie", cookie);
		if (apiKey is not null) req.Headers.Add("X-Api-Key", apiKey);
		return await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
	}

	// The response is live by the time this returns: the handler flushes the SSE headers only AFTER it
	// has registered its subscription, so an event ingested after this point cannot be missed by it.
	async Task<Tail> OpenTailAsync(string url, string? cookie, string? apiKey, CancellationToken ct)
	{
		var resp = await OpenRawAsync(url, cookie, apiKey, ct);
		resp.StatusCode.Should().Be(HttpStatusCode.OK, "the tail must open");
		resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
		return new Tail(resp, new StreamReader(await resp.Content.ReadAsStreamAsync(ct)));
	}

	// The `data:` payloads (one rendered <tr> each), in arrival order, until `count` of them arrive or
	// the token trips. A shortfall is returned, not thrown — the caller asserts on what actually came.
	static async Task<List<string>> ReadRowsAsync(Tail tail, int count, CancellationToken ct)
	{
		var rows = new List<string>();
		try
		{
			while (rows.Count < count && await tail.Reader.ReadLineAsync(ct) is { } line)
			{
				if (line.StartsWith("data: ", StringComparison.Ordinal))
					rows.Add(line[6..]);
			}
		}
		catch (OperationCanceledException) { }
		return rows;
	}

	static long IdOf(string row)
	{
		const string Marker = "data-event-id=\"";
		var start = row.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length;
		var end = row.IndexOf('"', start);
		return long.Parse(row[start..end], System.Globalization.CultureInfo.InvariantCulture);
	}

	// ---- seeding ----

	// Rows written straight to the log db: committed, never broadcast. This is precisely the state the
	// page renders from — "already on screen" — and, for the catch-up tests, "landed while the browser
	// was between the table render and the EventSource handshake".
	async Task<long> InsertAsync(string project, string message, long timestampMs)
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		using var logDb = store.NewEnsuredContext(project, LiveTailFixture.Log);
		return await logDb.InsertWithInt64IdentityAsync(new LogEntryRecord
		{
			ServiceKey = "seed",
			TimestampMs = timestampMs,
			Level = (int)LogLevel.Information,
			Message = message,
			MessageTemplate = message,
			PropertiesJson = "{}",
		});
	}

	// The REAL ingest path (insert → publish), i.e. what actually wakes a live subscriber. `level`
	// defaults to Information; the KQL filter tests need to ingest at other levels.
	async Task IngestAsync(string project, string message, LogLevel level = LogLevel.Information)
	{
		var pipeline = _fx.Factory.Services.GetRequiredService<IIngestionPipeline>();
		await pipeline.IngestAsync(project, LiveTailFixture.Log, [new LogEntryCandidate
		{
			ServiceKey = "live",
			Timestamp = DateTime.UtcNow,
			Level = level,
			Message = message,
			MessageTemplate = message,
			Properties = "{}",
		}], CancellationToken.None);
	}

	static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(20));

	// ---- the transport: a browser session gets events at all ----

	[Fact]
	public async Task Cookie_session_of_a_workspace_member_receives_a_live_event()
	{
		var auth = await LoginAsync("tail-member");
		using var cts = Deadline();

		using var tail = await OpenTailAsync(Url(LiveTailFixture.ProjA), auth, apiKey: null, cts.Token);

		var msg = $"live-{Guid.NewGuid():N}";
		await IngestAsync(LiveTailFixture.ProjA, msg);

		var rows = await ReadRowsAsync(tail, 1, cts.Token);
		rows.Should().ContainSingle(
			"a signed-in member of the project's workspace is the ONLY client this endpoint has — an "
			+ "EventSource cannot send X-Api-Key, so a cookie session that gets nothing is the whole bug");
		rows[0].Should().Contain(msg);
		IdOf(rows[0]).Should().BePositive(
			"the row id must be the real committed Id — a broadcast record comes back from BulkCopy with Id 0, "
			+ "which would break both the permalink and the cursor");
	}

	// The cross-tenant surface of the whole change: the endpoint has NO {workspaceKey} in its route, so
	// nothing but this check stands between a signed-in user and another tenant's log stream.
	[Fact]
	public async Task Cookie_session_cannot_tail_a_project_of_another_workspace()
	{
		var auth = await LoginAsync("tail-member");
		using var cts = Deadline();

		using var resp = await OpenRawAsync(Url(LiveTailFixture.ProjB), auth, apiKey: null, cts.Token);

		// The app's own denial shape for a signed-in user (WorkspaceAccessIsolationTests): 302 to
		// /AccessDenied — a real 403 page, never /Login. Asserted precisely rather than as "not 200",
		// which any accidental redirect would also satisfy.
		resp.StatusCode.Should().Be(HttpStatusCode.Redirect,
			"tail-member holds a role in wsa only — wsb's project must not stream to it");
		resp.Headers.Location!.ToString().Should().Contain("/AccessDenied");
		resp.Content.Headers.ContentType?.MediaType.Should().NotBe("text/event-stream");
	}

	[Fact]
	public async Task Anonymous_cannot_tail_anything()
	{
		using var cts = Deadline();
		using var resp = await OpenRawAsync(Url(LiveTailFixture.ProjA), cookie: null, apiKey: null, cts.Token);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect,
			"the cookie scheme is an ADDITION to the policy, not a hole in it — an unauthenticated tail is challenged");
		resp.Headers.Location!.ToString().Should().Contain("/Login");
		resp.Content.Headers.ContentType?.MediaType.Should().NotBe("text/event-stream");
	}

	// ---- the api-key path is not weakened by the cookie door ----

	[Fact]
	public async Task ApiKey_with_logs_query_still_streams()
	{
		using var cts = Deadline();
		using var tail = await OpenTailAsync(Url(LiveTailFixture.ProjA), cookie: null, LiveTailFixture.KeyQuery, cts.Token);

		var msg = $"key-{Guid.NewGuid():N}";
		await IngestAsync(LiveTailFixture.ProjA, msg);

		var rows = await ReadRowsAsync(tail, 1, cts.Token);
		rows.Should().ContainSingle("the api-key path must keep working");
		rows[0].Should().Contain(msg);
	}

	[Fact]
	public async Task ApiKey_without_logs_query_is_refused()
	{
		using var cts = Deadline();
		using var resp = await OpenRawAsync(Url(LiveTailFixture.ProjA), cookie: null, LiveTailFixture.KeyNoScope, cts.Token);

		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"admitting the cookie scheme must not let an api key past the scope gate it fails");
	}

	[Fact]
	public async Task ApiKey_of_another_project_is_refused()
	{
		using var cts = Deadline();
		using var resp = await OpenRawAsync(Url(LiveTailFixture.ProjB), cookie: null, LiveTailFixture.KeyQuery, cts.Token);

		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"a key scoped to tailproja must not tail tailprojb");
	}

	// ---- catch-up: no hole between the last rendered row and the subscription ----

	[Fact]
	public async Task Events_written_between_the_render_and_the_subscription_are_caught_up()
	{
		var auth = await LoginAsync("tail-member");
		using var cts = Deadline();
		var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

		// What the table rendered.
		var onScreen = await InsertAsync(LiveTailFixture.ProjA, $"rendered-{Guid.NewGuid():N}", now);
		// What landed AFTER that render but BEFORE the tail was switched on — the silent hole.
		var missedMsg = $"missed-{Guid.NewGuid():N}";
		var missedId = await InsertAsync(LiveTailFixture.ProjA, missedMsg, now + 1);

		var since = new LogCursor(now, onScreen).Encode();
		using var tail = await OpenTailAsync(Url(LiveTailFixture.ProjA, since), auth, apiKey: null, cts.Token);

		var rows = await ReadRowsAsync(tail, 1, cts.Token);
		rows.Should().ContainSingle("everything newer than the last rendered row is owed to the client");
		rows[0].Should().Contain(missedMsg);
		IdOf(rows[0]).Should().Be(missedId);
	}

	// The cursor is (Timestamp, Id), not Timestamp: a batch ingested inside one millisecond shares a
	// timestamp, and a timestamp-only cursor would re-serve the boundary row (duplicate) or skip past it
	// (loss). Three rows in the SAME millisecond, cursor on the middle one: the third arrives, exactly
	// once; the first two — already on screen — do not come back.
	[Fact]
	public async Task Equal_timestamps_are_neither_duplicated_nor_lost()
	{
		var auth = await LoginAsync("tail-member");
		using var cts = Deadline();
		var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

		var marker = Guid.NewGuid().ToString("N");
		var first = await InsertAsync(LiveTailFixture.ProjA, $"same-ms-1-{marker}", ts);
		var second = await InsertAsync(LiveTailFixture.ProjA, $"same-ms-2-{marker}", ts);
		var third = await InsertAsync(LiveTailFixture.ProjA, $"same-ms-3-{marker}", ts);

		var since = new LogCursor(ts, second).Encode();
		using var tail = await OpenTailAsync(Url(LiveTailFixture.ProjA, since), auth, apiKey: null, cts.Token);

		// Ask for two: only one may exist. The read runs to the deadline, so a duplicate would show up.
		using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
		var rows = await ReadRowsAsync(tail, 2, readCts.Token);

		rows.Should().ContainSingle("only the row AFTER the cursor is owed, and it is owed once");
		IdOf(rows[0]).Should().Be(third);
		rows[0].Should().NotContain($"same-ms-2-{marker}", "the row the cursor points AT is already on screen");
		rows.Should().NotContain(r => IdOf(r) == first, "a row older than the cursor must never be re-sent");
	}

	// Nothing on screen (an empty table) → no cursor → the stream starts at the tip: only what happens
	// from now on. The log already holds rows from the tests above, so a stream that ignored the tip
	// would dump history into an empty table.
	[Fact]
	public async Task With_no_cursor_the_stream_starts_at_the_tip()
	{
		var auth = await LoginAsync("tail-member");
		using var cts = Deadline();

		await InsertAsync(LiveTailFixture.ProjA, $"history-{Guid.NewGuid():N}", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

		using var tail = await OpenTailAsync(Url(LiveTailFixture.ProjA), auth, apiKey: null, cts.Token);

		var msg = $"after-{Guid.NewGuid():N}";
		await IngestAsync(LiveTailFixture.ProjA, msg);

		var rows = await ReadRowsAsync(tail, 1, cts.Token);
		rows.Should().ContainSingle();
		rows[0].Should().Contain(msg, "an empty table is owed only what happens from now on — not the log's backlog");
	}

	// ---- the tail applies the SAME ?kql= the table applies ----
	//
	// live-tail-sse-transport-broken, phase 2: the tail used to ignore ?kql= entirely and stream every
	// event regardless of the table's filter — exactly the moment the filter is needed, since the user
	// just armed the toggle to watch through it. KqlTransformer.ApplyRowFilters (reused, not
	// reimplemented — the same predicate compiler QueryLogsAsync/log_query use) is what LiveTailAsync now
	// runs the `where` clause through before a row is ever sent.

	[Fact]
	public async Task A_row_matching_the_filter_is_delivered()
	{
		var auth = await LoginAsync("tail-member");
		using var cts = Deadline();
		using var tail = await OpenTailAsync(Url(LiveTailFixture.ProjA, kql: "events | where Level >= 4"), auth, apiKey: null, cts.Token);

		var msg = $"matches-{Guid.NewGuid():N}";
		await IngestAsync(LiveTailFixture.ProjA, msg, LogLevel.Error);

		var rows = await ReadRowsAsync(tail, 1, cts.Token);
		rows.Should().ContainSingle("an Error event matches 'Level >= 4'");
		rows[0].Should().Contain(msg);
	}

	[Fact]
	public async Task A_row_not_matching_the_filter_is_not_delivered()
	{
		var auth = await LoginAsync("tail-member");
		using var cts = Deadline();
		using var tail = await OpenTailAsync(Url(LiveTailFixture.ProjA, kql: "events | where Level >= 4"), auth, apiKey: null, cts.Token);

		var filteredOut = $"below-threshold-{Guid.NewGuid():N}";
		await IngestAsync(LiveTailFixture.ProjA, filteredOut, LogLevel.Information);
		// A matching event AFTER it proves the stream is alive and simply chose not to deliver the first
		// one — a hang here would be indistinguishable from "correctly filtered", which is not a proof.
		var matches = $"above-threshold-{Guid.NewGuid():N}";
		await IngestAsync(LiveTailFixture.ProjA, matches, LogLevel.Error);

		var rows = await ReadRowsAsync(tail, 1, cts.Token);
		rows.Should().ContainSingle("only the matching event is owed");
		rows[0].Should().Contain(matches);
		rows[0].Should().NotContain(filteredOut, "an Information event must not pass 'Level >= 4'");
	}

	// Catch-up composes WITH the filter, not instead of it: the cursor's "everything since the last
	// rendered row" and the kql's "only what matches" are a conjunction — a strict superset of either
	// gate applied alone would either leak unfiltered history or drop matching history.
	[Fact]
	public async Task Catchup_and_filter_compose_missed_and_matching_arrives_missed_and_not_matching_does_not()
	{
		var auth = await LoginAsync("tail-member");
		using var cts = Deadline();
		var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

		var onScreen = await InsertAsync(LiveTailFixture.ProjA, $"rendered-{Guid.NewGuid():N}", now);
		// Missed AND matching: must be caught up.
		var missedMatch = $"missed-match-{Guid.NewGuid():N}";
		var missedMatchId = await InsertMatchingAsync(LiveTailFixture.ProjA, missedMatch, now + 1, LogLevel.Error);
		// Missed but NOT matching: must not be caught up.
		var missedNoMatch = $"missed-nomatch-{Guid.NewGuid():N}";
		await InsertMatchingAsync(LiveTailFixture.ProjA, missedNoMatch, now + 2, LogLevel.Information);

		var since = new LogCursor(now, onScreen).Encode();
		using var tail = await OpenTailAsync(
			Url(LiveTailFixture.ProjA, since, kql: "events | where Level >= 4"), auth, apiKey: null, cts.Token);

		var rows = await ReadRowsAsync(tail, 1, cts.Token);
		rows.Should().ContainSingle("exactly the missed row that ALSO matches is owed");
		IdOf(rows[0]).Should().Be(missedMatchId);
		rows[0].Should().NotContain(missedNoMatch, "missed but non-matching must not ride along with the catch-up");
	}

	async Task<long> InsertMatchingAsync(string project, string message, long timestampMs, LogLevel level)
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		using var logDb = store.NewEnsuredContext(project, LiveTailFixture.Log);
		return await logDb.InsertWithInt64IdentityAsync(new LogEntryRecord
		{
			ServiceKey = "seed",
			TimestampMs = timestampMs,
			Level = (int)level,
			Message = message,
			MessageTemplate = message,
			PropertiesJson = "{}",
		});
	}

	// ---- KQL that has no per-row form: refused up front, not silently unfiltered ----
	//
	// The maintainer's call, spelled out: an aggregate/reshaping query (summarize/project/distinct/
	// join/lookup/mv-expand/parse/count) has no defined "live tail of it" — there is no per-row stream
	// for a moving aggregate to be. The Logs page ALREADY never offers the toggle for such a query
	// (Index.cshtml hides it whenever IsShapeChanged), so this is the defense for anyone who builds the
	// URL by hand (an api-key client, a saved link, a bug elsewhere in the client). The response is a
	// normal 400 with a message naming the reason — never a silent fallback to the unfiltered firehose,
	// and never an aborted stream (rejected BEFORE any SSE header is written).
	[Fact]
	public async Task Aggregate_query_is_refused_with_a_400_not_an_unfiltered_stream()
	{
		var auth = await LoginAsync("tail-member");
		using var cts = Deadline();
		using var resp = await OpenRawAsync(
			Url(LiveTailFixture.ProjA, kql: "events | summarize count() by Level"), auth, apiKey: null, cts.Token);

		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
			"an aggregate has no per-row form — it must be refused, not silently streamed unfiltered");
		resp.Content.Headers.ContentType?.MediaType.Should().NotBe("text/event-stream");
		var body = await resp.Content.ReadAsStringAsync(cts.Token);
		body.Should().ContainAny("summarize", "row shape", "shape");
	}

	// order/take/top are DROPPED, not rejected and not applied: they bound a one-shot materialized
	// result and have no meaning against an unbounded stream (LiveTailAsync owns its own ordering and
	// batching via the cursor). A `where` alongside them still filters — dropping take/order must not
	// silently disable filtering too.
	[Fact]
	public async Task Take_and_order_are_dropped_but_the_where_clause_still_filters()
	{
		var auth = await LoginAsync("tail-member");
		using var cts = Deadline();
		using var tail = await OpenTailAsync(
			Url(LiveTailFixture.ProjA, kql: "events | where Level >= 4 | order by Timestamp desc | take 5"),
			auth, apiKey: null, cts.Token);

		var filteredOut = $"dropped-take-nomatch-{Guid.NewGuid():N}";
		await IngestAsync(LiveTailFixture.ProjA, filteredOut, LogLevel.Information);
		var matches = $"dropped-take-match-{Guid.NewGuid():N}";
		await IngestAsync(LiveTailFixture.ProjA, matches, LogLevel.Error);

		var rows = await ReadRowsAsync(tail, 1, cts.Token);
		rows.Should().ContainSingle("the request must still open and still filter despite carrying order/take");
		rows[0].Should().Contain(matches);
	}
}
