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
			c => new SessionsDb(SessionsDb.CreateOptions(c)), TestSchema.Sessions);
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

	// spec listing-tail-reachable (card listing-keyset-memory-sessions): the sessions listing
	// pages by KEYSET, not offset — a token naming the last row EMITTED, not a skip count. Walk
	// the whole set page by page via NextCursor and confirm every session is reached exactly
	// once, in order, with no page number anywhere on the wire.
	[Fact]
	public async Task ListPage_KeysetWalksTheWholeSet_NoOffset()
	{
		for (var i = 1; i <= 12; i++)
			await _store.UpsertAsync("proj", Row($"s{i:00}", "body"));

		var seen = new List<string>();
		string? cursor = null;
		do
		{
			var page = await _store.ListPageAsync("proj", null, null, SessionSortField.Updated, false, cursor, 5);
			seen.AddRange(page.Headers.Select(h => h.SessionId));
			cursor = page.NextCursor;
		} while (cursor is not null);

		seen.Should().Equal("s01", "s02", "s03", "s04", "s05", "s06", "s07", "s08", "s09", "s10", "s11", "s12");
	}

	// The keyset boundary is a ROW, not a count: a session inserted or deleted before the
	// boundary must not shift what "the next page" means — the exact failure mode an offset has
	// (spec listing-tail-reachable's whole reason to exist).
	[Fact]
	public async Task ListPage_Cursor_IsImmuneToConcurrentInsertBeforeBoundary()
	{
		for (var i = 1; i <= 5; i++)
			await _store.UpsertAsync("proj", Row($"s{i:00}", "body"));

		var p0 = await _store.ListPageAsync("proj", null, null, SessionSortField.Updated, false, null, 2);
		p0.Headers.Select(h => h.SessionId).Should().Equal("s01", "s02");

		// Concurrent write: a new row lands BEFORE the boundary in sort order (SessionId "s00"
		// sorts ahead of everything already paged past). An OFFSET-based page 2 would silently
		// re-serve "s02" here; the cursor must not.
		await _store.UpsertAsync("proj", Row("s00", "body"));

		var p1 = await _store.ListPageAsync("proj", null, null, SessionSortField.Updated, false, p0.NextCursor, 2);
		p1.Headers.Select(h => h.SessionId).Should().Equal("s03", "s04");
	}

	[Fact]
	public async Task ListPage_Search_NarrowsBySessionIdOrAgent()
	{
		await _store.UpsertAsync("proj", Row("alpha-1", "body"));
		await _store.UpsertAsync("proj", Row("alpha-2", "body"));
		await _store.UpsertAsync("proj", Row("beta-1", "body"));

		var hit = await _store.ListPageAsync("proj", "alpha", null, SessionSortField.Updated, false, null, 25);
		hit.Headers.Select(h => h.SessionId).Should().Equal("alpha-1", "alpha-2");

		// The Agent column is searchable too ("claude-code" on every seeded row).
		var byAgent = await _store.ListPageAsync("proj", "claude", null, SessionSortField.Updated, false, null, 25);
		byAgent.Headers.Should().HaveCount(3);
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

		var byAgent = await _store.ListPageAsync("proj", null, "claude-code", SessionSortField.Updated, false, null, 25);
		byAgent.Headers.Select(h => h.SessionId).Should().Equal("s1", "s3");

		// A prefix/substring must NOT match — this is an exact filter, not the `search` LIKE.
		var noHit = await _store.ListPageAsync("proj", null, "claude", SessionSortField.Updated, false, null, 25);
		noHit.Headers.Should().BeEmpty();
	}

	// card ui-search-sessions-hybrid: sort is a real SQL ORDER BY over updated/created/length
	// (Version — the message count — is the cheap length proxy; see SessionSortField).
	[Fact]
	public async Task ListPage_Sort_ByCreatedAndLength_BothDirections()
	{
		var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		// SessionId order (b, a, c) is deliberately NOT the Created/Version order, so a passing
		// assertion proves the sort actually reordered rather than happening to agree.
		await _store.UpsertAsync("proj", Row("b", "body") with { Created = t0.AddDays(2), Version = 5 });
		await _store.UpsertAsync("proj", Row("a", "body") with { Created = t0, Version = 20 });
		await _store.UpsertAsync("proj", Row("c", "body") with { Created = t0.AddDays(1), Version = 1 });

		var byCreatedAsc = await _store.ListPageAsync("proj", null, null, SessionSortField.Created, false, null, 25);
		byCreatedAsc.Headers.Select(h => h.SessionId).Should().Equal("a", "c", "b");

		var byCreatedDesc = await _store.ListPageAsync("proj", null, null, SessionSortField.Created, true, null, 25);
		byCreatedDesc.Headers.Select(h => h.SessionId).Should().Equal("b", "c", "a");

		var byLengthAsc = await _store.ListPageAsync("proj", null, null, SessionSortField.Length, false, null, 25);
		byLengthAsc.Headers.Select(h => h.SessionId).Should().Equal("c", "b", "a");
	}

	// spec listing-tail-reachable: Version (the length proxy) is far from unique — several
	// sessions can share a message count. Without SessionId as a secondary key the keyset
	// boundary would be ambiguous (which of the tied rows was "last emitted"?) and a walk could
	// skip or repeat a tied row. Three sessions tie at Version=1; paging one at a time must still
	// reach all three, in a stable SessionId order, exactly once each.
	[Fact]
	public async Task ListPage_Cursor_DisambiguatesTiedSortValue_BySessionId()
	{
		await _store.UpsertAsync("proj", Row("tie-c", "body") with { Version = 1 });
		await _store.UpsertAsync("proj", Row("tie-a", "body") with { Version = 1 });
		await _store.UpsertAsync("proj", Row("tie-b", "body") with { Version = 1 });

		var seen = new List<string>();
		string? cursor = null;
		do
		{
			var page = await _store.ListPageAsync("proj", null, null, SessionSortField.Length, false, cursor, 1);
			seen.AddRange(page.Headers.Select(h => h.SessionId));
			cursor = page.NextCursor;
		} while (cursor is not null);

		seen.Should().Equal("tie-a", "tie-b", "tie-c"); // tiebreak: SessionId ascending
	}

	// spec listing-tail-reachable: a token issued for one search/agent/sort/direction must be
	// REFUSED against a different one — never silently restarted under a new ordering (the
	// KeysetCursor.Decode contract tasks_search's listing mode also relies on).
	[Fact]
	public async Task ListPage_Cursor_RejectsMismatchedQuery()
	{
		await _store.UpsertAsync("proj", Row("s1", "body"));
		await _store.UpsertAsync("proj", Row("s2", "body"));

		var page = await _store.ListPageAsync("proj", null, null, SessionSortField.Updated, false, null, 1);
		page.NextCursor.Should().NotBeNull();

		// Same cursor, DIFFERENT sort axis — must throw, not splice.
		var act = () => _store.ListPageAsync("proj", null, null, SessionSortField.Created, false, page.NextCursor, 1);
		await act.Should().ThrowAsync<ArgumentException>();

		// Same cursor, DIFFERENT agent filter — must also throw.
		var act2 = () => _store.ListPageAsync("proj", null, "codex", SessionSortField.Updated, false, page.NextCursor, 1);
		await act2.Should().ThrowAsync<ArgumentException>();
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
