using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;
using PetBox.Sessions.Services;

namespace PetBox.Tests.Sessions;

// Covers the converged session write path: the MCP session_upsert tool and the REST Stop-hook
// endpoint both delegate to ISessionService, so testing the service exercises the one path both
// surfaces share (the arch test proves neither bypasses it). Latest-snapshot, last-write-wins.
public sealed class SessionServiceTests : IDisposable
{
	readonly string _dir;
	readonly ScopedDbFactory<SessionsDb> _factory;
	readonly SessionService _svc;

	public SessionServiceTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-sessionsvc-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_factory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), SessionsSchema.Ensure);
		_svc = new SessionService(new SessionStore(_factory));
	}

	public void Dispose()
	{
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static SessionMessageInput[] Msgs(params (string Role, string Content)[] m) =>
		m.Select(x => new SessionMessageInput(x.Role, x.Content)).ToArray();

	[Fact]
	public async Task Upsert_Then_Get_And_List_ThroughService()
	{
		var o = await _svc.UpsertAsync("proj", "s1", "claude-code", Msgs(("session", "# plan v1")));
		o.Version.Should().Be(1);
		o.MessageCount.Should().Be(1);

		(await _svc.GetAsync("proj", "s1"))!.Content.Should().Be("# plan v1");
		(await _svc.ListAsync("proj")).Select(s => s.SessionId).Should().Equal("s1");
	}

	[Fact]
	public async Task Upsert_IsLatestSnapshot_AndNumbersMessages()
	{
		var o = await _svc.UpsertAsync("proj", "s1", "claude-code", Msgs(("user", "hello"), ("assistant", "hi")));
		o.Version.Should().Be(2);   // version == last message ordinal
		o.MessageCount.Should().Be(2);

		// Re-push the grown transcript → latest snapshot replaces; version tracks the last ordinal.
		var o2 = await _svc.UpsertAsync("proj", "s1", "claude-code",
			Msgs(("user", "hello"), ("assistant", "hi"), ("user", "more")));
		o2.Version.Should().Be(3);

		var snap = await _svc.GetAsync("proj", "s1");
		snap!.Messages.Should().HaveCount(3);
		snap.Content.Should().Contain("### user").And.Contain("more"); // multi-message renders with headers
	}

	// ---- session_append: server-authoritative cursor (spec session-append-wire) ----------

	[Fact]
	public async Task Append_NewSession_StartsAtOrdinalOne()
	{
		var o = await _svc.AppendAsync("proj", "s1", "claude-code", fromOrdinal: 1, Msgs(("user", "a"), ("assistant", "b")));
		o.Applied.Should().BeTrue();
		o.LastOrdinal.Should().Be(2);
		o.Appended.Should().Be(2);

		var snap = await _svc.GetAsync("proj", "s1");
		snap!.Messages.Select(m => (m.Version, m.Content)).Should().Equal((1L, "a"), (2L, "b"));
	}

	[Fact]
	public async Task Append_Contiguous_ExtendsExistingSession()
	{
		await _svc.AppendAsync("proj", "s1", "claude-code", 1, Msgs(("user", "a"), ("assistant", "b")));

		var o = await _svc.AppendAsync("proj", "s1", "claude-code", 3, Msgs(("user", "c")));
		o.Applied.Should().BeTrue();
		o.LastOrdinal.Should().Be(3);
		o.Appended.Should().Be(1);

		var snap = await _svc.GetAsync("proj", "s1");
		snap!.Messages.Should().HaveCount(3);
		snap.Messages.Select(m => m.Version).Should().Equal(1, 2, 3);
		snap.Messages[2].Content.Should().Be("c");
	}

	[Fact]
	public async Task Append_Overlap_IsIdempotent_NoDuplicates_TailAppends()
	{
		await _svc.AppendAsync("proj", "s1", "claude-code", 1, Msgs(("user", "a"), ("assistant", "b")));

		// Exact re-send of already-stored ordinals: nothing appended, cursor unchanged, no write.
		var repeat = await _svc.AppendAsync("proj", "s1", "claude-code", 1, Msgs(("user", "a"), ("assistant", "b")));
		repeat.Applied.Should().BeTrue();
		repeat.LastOrdinal.Should().Be(2);
		repeat.Appended.Should().Be(0);
		(await _svc.GetAsync("proj", "s1"))!.Messages.Should().HaveCount(2, "idempotent overlap must not duplicate");

		// Overlapping batch with a new tail: the known ordinals are skipped (stored content
		// wins — the divergent "B!" does NOT rewrite ordinal 2), the tail appends.
		var o = await _svc.AppendAsync("proj", "s1", "claude-code", 2, Msgs(("assistant", "B!"), ("user", "c"), ("assistant", "d")));
		o.Applied.Should().BeTrue();
		o.LastOrdinal.Should().Be(4);
		o.Appended.Should().Be(2);

		var snap = await _svc.GetAsync("proj", "s1");
		snap!.Messages.Select(m => m.Content).Should().Equal("a", "b", "c", "d");
	}

	[Fact]
	public async Task Append_Gap_WritesNothing_ReturnsServerCursor()
	{
		await _svc.AppendAsync("proj", "s1", "claude-code", 1, Msgs(("user", "a")));

		// fromOrdinal 3 > lastOrdinal(1)+1 → structural reject with the cursor to resend from.
		var o = await _svc.AppendAsync("proj", "s1", "claude-code", 3, Msgs(("user", "c")));
		o.Applied.Should().BeFalse();
		o.LastOrdinal.Should().Be(1, "the reject must carry the server cursor");
		o.Appended.Should().Be(0);
		(await _svc.GetAsync("proj", "s1"))!.Messages.Should().HaveCount(1, "a gap must write nothing");

		// A missing session's cursor is 0: any fromOrdinal > 1 is a gap.
		var missing = await _svc.AppendAsync("proj", "nope", "claude-code", 2, Msgs(("user", "x")));
		missing.Applied.Should().BeFalse();
		missing.LastOrdinal.Should().Be(0);
		(await _svc.GetAsync("proj", "nope")).Should().BeNull();
	}

	[Fact]
	public async Task Append_Series_ThenGet_YieldsFullDialogue()
	{
		await _svc.AppendAsync("proj", "s1", "claude-code", 1, Msgs(("user", "q1"), ("assistant", "a1")));
		await _svc.AppendAsync("proj", "s1", "claude-code", 3, Msgs(("user", "q2"), ("assistant", "a2")));
		await _svc.AppendAsync("proj", "s1", "claude-code", 5, Msgs(("user", "q3")));

		var snap = await _svc.GetAsync("proj", "s1");
		snap!.Version.Should().Be(5);
		snap.Messages.Select(m => m.Version).Should().Equal(1, 2, 3, 4, 5);
		snap.Messages.Select(m => m.Content).Should().Equal("q1", "a1", "q2", "a2", "q3");
		snap.Content.Should().Contain("### user").And.Contain("q3"); // renders like any snapshot
	}

	[Fact]
	public async Task Upsert_OverAppendedSession_ReplacesSnapshot_RepairPath()
	{
		await _svc.AppendAsync("proj", "s1", "claude-code", 1, Msgs(("user", "old1"), ("assistant", "old2"), ("user", "old3")));

		// The full-snapshot PUT stays the repair/import path: it replaces the appended state.
		var o = await _svc.UpsertAsync("proj", "s1", "claude-code", Msgs(("user", "fixed")));
		o.Version.Should().Be(1);

		var snap = await _svc.GetAsync("proj", "s1");
		snap!.Messages.Should().HaveCount(1);
		snap.Content.Should().Be("fixed");

		// And append continues from the repaired cursor.
		var next = await _svc.AppendAsync("proj", "s1", "claude-code", 2, Msgs(("assistant", "more")));
		next.Applied.Should().BeTrue();
		next.LastOrdinal.Should().Be(2);
	}

	[Fact]
	public async Task Append_FromOrdinalBelowOne_Throws()
	{
		var act = () => _svc.AppendAsync("proj", "s1", "claude-code", 0, Msgs(("user", "a")));
		await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
	}

	[Fact]
	public async Task Delta_ReturnsOnlyMessagesAfterCursor()
	{
		await _svc.UpsertAsync("proj", "s1", "claude-code", Msgs(("user", "a"), ("assistant", "b"), ("user", "c")));

		var delta = await _svc.DeltaAsync("proj", "s1", sinceVersion: 1);
		delta.Select(m => m.Version).Should().Equal(2, 3);
		delta.Select(m => m.Content).Should().Equal("b", "c");
	}

	[Fact]
	public async Task Delete_ThroughService_HidesEverywhere_ThenUpsertResurrects()
	{
		await _svc.UpsertAsync("proj", "s1", "claude-code", Msgs(("user", "a")));

		(await _svc.DeleteAsync("proj", "s1")).Should().BeTrue();
		(await _svc.DeleteAsync("proj", "s1")).Should().BeFalse(); // idempotent

		(await _svc.GetAsync("proj", "s1")).Should().BeNull();
		(await _svc.ListAsync("proj")).Should().BeEmpty();
		(await _svc.DeltaAsync("proj", "s1", 0)).Should().BeEmpty(); // delta rides Get

		var o = await _svc.UpsertAsync("proj", "s1", "claude-code", Msgs(("user", "a"), ("assistant", "b")));
		o.Version.Should().Be(2);
		(await _svc.GetAsync("proj", "s1")).Should().NotBeNull(); // resurrected
	}
}
