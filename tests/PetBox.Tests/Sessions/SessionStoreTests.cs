using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;

namespace PetBox.Tests.Sessions;

public sealed class SessionStoreTests : IDisposable
{
	readonly string _dir;
	readonly ScopedDbFactory<SessionsDb> _factory;
	readonly SessionStore _store;

	public SessionStoreTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-sessions-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_factory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), SessionsSchema.Ensure);
		_store = new SessionStore(_factory);
	}

	public void Dispose()
	{
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static SessionRow Row(string id, string text) => new()
	{
		SessionId = id,
		Agent = "claude-code",
		ContentZ = SessionContent.Encode(new[] { new SessionMessage(1, "session", text) }),
		Version = 1,
		Updated = DateTime.UtcNow,
		Created = DateTime.UtcNow,
	};

	[Fact]
	public void GetContext_AutoVivifies_ProjectFile()
	{
		_ = _store.GetContext("proj");
		File.Exists(ScopedDbFiles.PathFor(_factory.BaseDir, "proj", null)).Should().BeTrue();
	}

	[Fact]
	public async Task Upsert_Then_Get_And_List()
	{
		await _store.UpsertAsync("proj", Row("s1", "# plan v1"));

		(await _store.GetAsync("proj", "s1"))!.Content.Should().Be("# plan v1"); // single message → verbatim
		(await _store.ListAsync("proj")).Select(s => s.SessionId).Should().Equal("s1");
	}

	[Fact]
	public async Task Upsert_SameSession_DoesNotGrowRows()
	{
		await _store.UpsertAsync("proj", Row("s1", "v1"));
		await _store.UpsertAsync("proj", Row("s1", "v2"));

		_store.GetContext("proj").Sessions.Count().Should().Be(1); // latest-snapshot: no history
		(await _store.GetAsync("proj", "s1"))!.Content.Should().Be("v2");
	}

	[Fact]
	public async Task Delete_IsSoft_HiddenFromReads_RowKept()
	{
		await _store.UpsertAsync("proj", Row("s1", "v1"));
		await _store.UpsertAsync("proj", Row("s2", "v1"));

		(await _store.DeleteAsync("proj", "s1")).Should().BeTrue();

		(await _store.GetAsync("proj", "s1")).Should().BeNull();
		(await _store.ListAsync("proj")).Select(s => s.SessionId).Should().Equal("s2");
		// Soft: the row survives with the marker + audit stamp.
		var raw = _store.GetContext("proj").Sessions.Single(s => s.SessionId == "s1");
		raw.IsDeleted.Should().BeTrue();
		raw.DeletedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task Delete_Missing_Or_Repeated_ReturnsFalse()
	{
		(await _store.DeleteAsync("proj", "ghost")).Should().BeFalse();

		await _store.UpsertAsync("proj", Row("s1", "v1"));
		(await _store.DeleteAsync("proj", "s1")).Should().BeTrue();
		(await _store.DeleteAsync("proj", "s1")).Should().BeFalse(); // idempotent
	}

	// spec ui-list-pagination: the sessions list pages server-side (OFFSET/LIMIT) and
	// filters by a substring over SessionId/Agent — never loading the whole set to page.
	[Fact]
	public async Task ListPage_AppliesOffset_AndReportsHasNext()
	{
		for (var i = 1; i <= 12; i++)
			await _store.UpsertAsync("proj", Row($"s{i:00}", "body"));

		var p0 = await _store.ListPageAsync("proj", null, pageNum: 0, pageSize: 5);
		p0.Total.Should().Be(12);
		p0.HasNext.Should().BeTrue();
		p0.Headers.Select(h => h.SessionId).Should().Equal("s01", "s02", "s03", "s04", "s05");

		var p1 = await _store.ListPageAsync("proj", null, pageNum: 1, pageSize: 5);
		p1.HasNext.Should().BeTrue();
		p1.Headers.Select(h => h.SessionId).Should().Equal("s06", "s07", "s08", "s09", "s10");
		// The second page is a different slice — the OFFSET actually moved.
		p1.Headers.Should().NotContain(h => h.SessionId == "s01");

		var p2 = await _store.ListPageAsync("proj", null, pageNum: 2, pageSize: 5);
		p2.HasNext.Should().BeFalse(); // last (partial) page
		p2.Headers.Select(h => h.SessionId).Should().Equal("s11", "s12");
	}

	[Fact]
	public async Task ListPage_Search_NarrowsBySessionIdOrAgent()
	{
		await _store.UpsertAsync("proj", Row("alpha-1", "body"));
		await _store.UpsertAsync("proj", Row("alpha-2", "body"));
		await _store.UpsertAsync("proj", Row("beta-1", "body"));

		var hit = await _store.ListPageAsync("proj", "alpha", pageNum: 0, pageSize: 25);
		hit.Total.Should().Be(2);
		hit.Headers.Select(h => h.SessionId).Should().Equal("alpha-1", "alpha-2");

		// The Agent column is searchable too ("claude-code" on every seeded row).
		var byAgent = await _store.ListPageAsync("proj", "claude", pageNum: 0, pageSize: 25);
		byAgent.Total.Should().Be(3);
	}

	// card ui-search-sessions-hybrid: an exact `agent` filter (the UI's dropdown) is a DIFFERENT
	// predicate from the free-text `search` substring match above — combinable with it, never
	// confused with it (a substring match on "code" would also hit "claude-coder"; exact must not).
	[Fact]
	public async Task ListPage_AgentFilter_NarrowsToExactAgent()
	{
		await _store.UpsertAsync("proj", Row("s1", "body") with { Agent = "claude-code" });
		await _store.UpsertAsync("proj", Row("s2", "body") with { Agent = "codex" });
		await _store.UpsertAsync("proj", Row("s3", "body") with { Agent = "claude-code" });

		var byAgent = await _store.ListPageAsync("proj", null, pageNum: 0, pageSize: 25, agent: "claude-code");
		byAgent.Total.Should().Be(2);
		byAgent.Headers.Select(h => h.SessionId).Should().Equal("s1", "s3");

		// A prefix/substring must NOT match — this is an exact filter, not the `search` LIKE.
		var noHit = await _store.ListPageAsync("proj", null, pageNum: 0, pageSize: 25, agent: "claude");
		noHit.Total.Should().Be(0);
	}

	// card ui-search-sessions-hybrid: sort is a real SQL ORDER BY over updated/created/length
	// (Version — the message count — is the cheap length proxy; see SessionSortField). `sort:
	// null` (the default every pre-existing caller uses) keeps the untouched SessionId-asc order.
	[Fact]
	public async Task ListPage_Sort_ByCreatedAndLength_BothDirections()
	{
		var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		// SessionId order (b, a, c) is deliberately NOT the Created/Version order, so a passing
		// assertion proves the sort actually reordered rather than happening to agree.
		await _store.UpsertAsync("proj", Row("b", "body") with { Created = t0.AddDays(2), Version = 5 });
		await _store.UpsertAsync("proj", Row("a", "body") with { Created = t0, Version = 20 });
		await _store.UpsertAsync("proj", Row("c", "body") with { Created = t0.AddDays(1), Version = 1 });

		var byCreatedAsc = await _store.ListPageAsync("proj", null, 0, 25, sort: SessionSortField.Created, sortDesc: false);
		byCreatedAsc.Headers.Select(h => h.SessionId).Should().Equal("a", "c", "b");

		var byCreatedDesc = await _store.ListPageAsync("proj", null, 0, 25, sort: SessionSortField.Created, sortDesc: true);
		byCreatedDesc.Headers.Select(h => h.SessionId).Should().Equal("b", "c", "a");

		var byLengthAsc = await _store.ListPageAsync("proj", null, 0, 25, sort: SessionSortField.Length, sortDesc: false);
		byLengthAsc.Headers.Select(h => h.SessionId).Should().Equal("c", "b", "a");

		// No `sort` at all → the pre-existing default (SessionId asc), unchanged by this card.
		var legacyDefault = await _store.ListPageAsync("proj", null, 0, 25);
		legacyDefault.Headers.Select(h => h.SessionId).Should().Equal("a", "b", "c");
	}

	[Fact]
	public async Task Upsert_AfterDelete_Resurrects()
	{
		await _store.UpsertAsync("proj", Row("s1", "v1"));
		await _store.DeleteAsync("proj", "s1");

		await _store.UpsertAsync("proj", Row("s1", "v2")); // the hook re-pushes → replace row

		var snap = await _store.GetAsync("proj", "s1");
		snap.Should().NotBeNull();
		snap!.Content.Should().Be("v2");
		_store.GetContext("proj").Sessions.Single(s => s.SessionId == "s1").IsDeleted.Should().BeFalse();
	}
}
