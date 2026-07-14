using System.Net;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Log.Core.Data;

namespace PetBox.Tests.Web;

// live-tail-row-details-unexpandable: LogApi.RenderEvent streams a bare <tr class="event-live"> with
// no paired <tr class="event-details"> sibling, so a live row could never be expanded — not even after
// live tail was switched off. The fix is EventDetailsApi (Pages/Logs/EventDetails.cshtml.cs), fetched
// lazily by ts/logs.ts on a live row's first click. This suite pins its authorization (it reuses
// LogApi.AuthorizeProjectViewerAsync — the SAME gate LogLiveTailTests exercises for live-tail, since
// both routes carry no {workspaceKey} and are the identical cross-tenant surface) and its content (the
// SAME _EventDetails partial a non-live row already renders inline).
//
// Reuses LiveTailFixture (LogLiveTailTests.cs) rather than standing up a parallel one: same workspaces,
// projects, users and keys this endpoint needs the exact same authorization boundary for.
public sealed class LogEventDetailsApiTests : IClassFixture<LiveTailFixture>
{
	readonly LiveTailFixture _fx;
	readonly HttpClient _client;

	public LogEventDetailsApiTests(LiveTailFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	static string Url(string project, long id) =>
		$"/api/logs/{project}/{LiveTailFixture.Log}/events/{id}";

	async Task<string> LoginAsync(string username)
	{
		using var loginPage = await _client.GetAsync("/Login");
		var html = await loginPage.Content.ReadAsStringAsync();
		var tokenStart = html.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = html.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var valueEnd = html.IndexOf('"', valueStart);
		var token = html[valueStart..valueEnd];
		var afCookie = loginPage.Headers.GetValues("Set-Cookie")
			.First(c => c.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase))
			.Split(';')[0];

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

	async Task<HttpResponseMessage> GetAsync(string url, string? cookie, string? apiKey)
	{
		using var req = new HttpRequestMessage(HttpMethod.Get, url);
		if (cookie is not null) req.Headers.Add("Cookie", cookie);
		if (apiKey is not null) req.Headers.Add("X-Api-Key", apiKey);
		return await _client.SendAsync(req);
	}

	async Task<long> InsertEventAsync(string project, string message, string propertiesJson = "{}")
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var store = scope.ServiceProvider.GetRequiredService<ILogStore>();
		using var logDb = store.NewEnsuredContext(project, LiveTailFixture.Log);
		return await logDb.InsertWithInt64IdentityAsync(new LogEntryRecord
		{
			ServiceKey = "details-test",
			TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			Level = 2,
			Message = message,
			MessageTemplate = message,
			PropertiesJson = propertiesJson,
		});
	}

	// ---- content: the SAME markup a non-live row already carries inline ----

	[Fact]
	public async Task Cookie_session_of_a_workspace_member_gets_the_details_fragment()
	{
		var auth = await LoginAsync("tail-member");
		var msg = $"details-{Guid.NewGuid():N}";
		var id = await InsertEventAsync(
			LiveTailFixture.ProjA, msg, """{"Widget":"gizmo-42"}""");

		using var resp = await GetAsync(Url(LiveTailFixture.ProjA, id), auth, apiKey: null);

		resp.StatusCode.Should().Be(HttpStatusCode.OK, "a workspace member reading their own project's log must succeed");
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("event-details", "the fragment is the SAME <tr class=\"event-details\"> a non-live row renders");
		html.Should().Contain(id.ToString(System.Globalization.CultureInfo.InvariantCulture), "the Id field");
		html.Should().Contain("Widget", "a property key must be present");
		html.Should().Contain("gizmo-42", "a property value must be present");
		html.Should().Contain("filter-chip", "the eq/ne filter chips must come along — they live only in this partial");
	}

	[Fact]
	public async Task ApiKey_with_logs_query_gets_the_details_fragment()
	{
		var msg = $"details-key-{Guid.NewGuid():N}";
		var id = await InsertEventAsync(LiveTailFixture.ProjA, msg);

		using var resp = await GetAsync(Url(LiveTailFixture.ProjA, id), cookie: null, LiveTailFixture.KeyQuery);

		resp.StatusCode.Should().Be(HttpStatusCode.OK, "the api-key path must work exactly like it does for live-tail");
	}

	// ---- the cross-tenant surface: this route also has no {workspaceKey} ----

	[Fact]
	public async Task Cookie_session_cannot_read_details_from_a_project_of_another_workspace()
	{
		var auth = await LoginAsync("tail-member");
		var id = await InsertEventAsync(LiveTailFixture.ProjB, "cross-tenant-probe");

		using var resp = await GetAsync(Url(LiveTailFixture.ProjB, id), auth, apiKey: null);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect,
			"tail-member holds a role in wsa only — wsb's project details must not be readable");
		resp.Headers.Location!.ToString().Should().Contain("/AccessDenied");
	}

	[Fact]
	public async Task Anonymous_cannot_read_any_details()
	{
		var id = await InsertEventAsync(LiveTailFixture.ProjA, "anon-probe");

		using var resp = await GetAsync(Url(LiveTailFixture.ProjA, id), cookie: null, apiKey: null);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect,
			"the cookie scheme is an ADDITION to the policy, not a hole in it");
		resp.Headers.Location!.ToString().Should().Contain("/Login");
	}

	[Fact]
	public async Task ApiKey_without_logs_query_is_refused()
	{
		var id = await InsertEventAsync(LiveTailFixture.ProjA, "noscope-probe");

		using var resp = await GetAsync(Url(LiveTailFixture.ProjA, id), cookie: null, LiveTailFixture.KeyNoScope);

		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"admitting the cookie scheme must not let an api key past the scope gate it fails");
	}

	[Fact]
	public async Task ApiKey_of_another_project_is_refused()
	{
		var id = await InsertEventAsync(LiveTailFixture.ProjB, "wrong-project-probe");

		using var resp = await GetAsync(Url(LiveTailFixture.ProjB, id), cookie: null, LiveTailFixture.KeyQuery);

		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a key scoped to tailproja must not read tailprojb's events");
	}

	// ---- an id that does not exist in THIS project+log must not leak / must not 500 ----

	[Fact]
	public async Task Unknown_event_id_is_404_not_a_500()
	{
		var auth = await LoginAsync("tail-member");

		// Implausibly large — never assigned by any test's inserts, so this is unambiguously "absent
		// from this project+log", unlike an id borrowed from another project (each project+log is its
		// own SQLite file with its OWN identity sequence, so ids collide across projects trivially —
		// see Cross_project_id_never_returns_the_other_projects_row below for that case instead).
		using var resp = await GetAsync(Url(LiveTailFixture.ProjA, 999_999_999), auth, apiKey: null);

		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	// The real cross-tenant content check: an id that DOES exist, just in the OTHER project's log file.
	// Per-project-log SQLite files mean ProjA's query has no row to find at that id UNLESS its own
	// independent identity sequence happens to have assigned the same number to one of ITS OWN rows —
	// which is likely, not a corner case, so the assertion is on CONTENT (never ProjB's row), not on a
	// specific status code either way.
	[Fact]
	public async Task Cross_project_id_never_returns_the_other_projects_row()
	{
		var auth = await LoginAsync("tail-member");
		var marker = $"lives-only-in-b-{Guid.NewGuid():N}";
		var idInB = await InsertEventAsync(LiveTailFixture.ProjB, marker);

		using var resp = await GetAsync(Url(LiveTailFixture.ProjA, idInB), auth, apiKey: null);

		if (resp.StatusCode == HttpStatusCode.OK)
		{
			var html = await resp.Content.ReadAsStringAsync();
			html.Should().NotContain(marker,
				"ProjA's own identity sequence may coincidentally reuse this number for ITS OWN row, "
				+ "but that row's content must never be ProjB's");
		}
		else
		{
			resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
		}
	}

	[Fact]
	public async Task Nonexistent_log_is_404()
	{
		var auth = await LoginAsync("tail-member");

		using var resp = await GetAsync(
			$"/api/logs/{LiveTailFixture.ProjA}/does-not-exist/events/1", auth, apiKey: null);

		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}
}
