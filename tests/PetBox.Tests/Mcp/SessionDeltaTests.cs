using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Settings;
using PetBox.Sessions.Data;
using PetBox.Sessions.Services;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// session_delta completes the sessions uniform-entity-verbs matrix. Sessions are last-write-wins
// blobs with NO store-wide version watermark, so the cursor is the session `Updated` time as Unix
// epoch milliseconds (the real monotonic field, bumped on every write). This verifies the documented
// approximation: a change after the cursor surfaces; the cursor advances; the exclusive `>` boundary
// keeps a session AT the cursor from re-appearing.
public sealed class SessionDeltaTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly ScopedDbFactory<SessionsDb> _factory;
	readonly SessionService _sessions;

	public SessionDeltaTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-session-delta-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_factory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), SessionsSchema.Ensure);
		_sessions = new SessionService(new SessionStore(_factory));
	}

	public void Dispose()
	{
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static IHttpContextAccessor Http() =>
		new HttpContextAccessor
		{
			HttpContext = new DefaultHttpContext
			{
				User = new ClaimsPrincipal(new ClaimsIdentity(
					[new Claim("project", Proj), new Claim("scopes", "tasks:read,tasks:write")], "test")),
			},
		};

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Features:Tasks"] = "true" }).Build());

	Task Save(IHttpContextAccessor http, string sessionId, string content) =>
		SessionTools.UpsertAsync(http, Flags(), _sessions, Proj, sessionId, "claude-code", content);

	[Fact]
	public async Task Delta_SurfacesSessionsChangedSinceCursor()
	{
		var http = Http();

		await Save(http, "s-a", "alpha");
		// The whole archive since 0: A is present, and the cursor is A's Updated-ms.
		var all = await SessionTools.DeltaAsync(http, Flags(), _sessions, Proj, sinceVersion: 0);
		all.Items.Should().ContainSingle(i => i.SessionId == "s-a");
		all.CurrentVersion.Should().BeGreaterThan(0);
		var cursor = all.CurrentVersion;

		// Nothing has changed strictly after the cursor (exclusive `>`), so the delta is empty.
		var since = await SessionTools.DeltaAsync(http, Flags(), _sessions, Proj, sinceVersion: cursor);
		since.Items.Should().BeEmpty();

		// A distinct-millisecond later write (the documented granularity: advance the clock past the
		// coarse UtcNow tick) must surface as changed-since-cursor.
		await Task.Delay(25);
		await Save(http, "s-b", "bravo");

		var delta = await SessionTools.DeltaAsync(http, Flags(), _sessions, Proj, sinceVersion: cursor);
		delta.Items.Should().ContainSingle(i => i.SessionId == "s-b");
		delta.Items.Should().NotContain(i => i.SessionId == "s-a"); // A is at/behind the cursor
		delta.CurrentVersion.Should().BeGreaterThan(cursor);
	}

	[Fact]
	public async Task Delta_EmptyArchive_ZeroCursor_NoItems()
	{
		var res = await SessionTools.DeltaAsync(Http(), Flags(), _sessions, Proj, sinceVersion: 0);
		res.CurrentVersion.Should().Be(0);
		res.Items.Should().BeEmpty();
	}

	[Fact]
	public async Task Delta_ReUpsert_BumpsCursor_ReSurfacesSession()
	{
		var http = Http();
		await Save(http, "s-x", "one");
		var cursor = (await SessionTools.DeltaAsync(http, Flags(), _sessions, Proj, 0)).CurrentVersion;

		await Task.Delay(25);
		await Save(http, "s-x", "two"); // LWW re-push bumps Updated

		var delta = await SessionTools.DeltaAsync(http, Flags(), _sessions, Proj, cursor);
		delta.Items.Should().ContainSingle(i => i.SessionId == "s-x");
		delta.CurrentVersion.Should().BeGreaterThan(cursor);
	}
}
