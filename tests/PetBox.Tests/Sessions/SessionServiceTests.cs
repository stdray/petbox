using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;
using PetBox.Sessions.Services;

namespace PetBox.Tests.Sessions;

// Covers the converged session write path: the MCP session.upsert tool and the REST Stop-hook
// endpoint both delegate to ISessionService, so testing the service exercises the one path both
// surfaces share (the arch test proves neither bypasses it). Latest-snapshot, last-write-wins.
[Collection("DataModule")]
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
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
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

	[Fact]
	public async Task Delta_ReturnsOnlyMessagesAfterCursor()
	{
		await _svc.UpsertAsync("proj", "s1", "claude-code", Msgs(("user", "a"), ("assistant", "b"), ("user", "c")));

		var delta = await _svc.DeltaAsync("proj", "s1", sinceVersion: 1);
		delta.Select(m => m.Version).Should().Equal(2, 3);
		delta.Select(m => m.Content).Should().Equal("b", "c");
	}
}
