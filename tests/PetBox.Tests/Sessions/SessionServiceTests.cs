using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.Sessions.Data;
using PetBox.Sessions.Services;

namespace PetBox.Tests.Sessions;

// Covers the converged session write path: the MCP session.append tool and the REST
// Stop-hook endpoint both delegate to ISessionService, so testing the service exercises
// the one path both surfaces share (the arch test proves neither bypasses it). Includes
// the two calling styles — caller-supplied baseline (MCP) vs read-current-then-append
// (REST last-write-wins).
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

	[Fact]
	public async Task Append_Then_Get_And_List_ThroughService()
	{
		var r = await _svc.AppendAsync("proj", "s1", "claude-code", "# plan v1");
		r.Result.Applied.Should().BeTrue();

		(await _svc.GetAsync("proj", "s1"))!.Content.Should().Be("# plan v1");
		(await _svc.ListAsync("proj")).Select(s => s.Key).Should().Equal("s1");
	}

	[Fact]
	public async Task StaleBaseline_Conflicts_WhileReadCurrentThenAppend_Wins()
	{
		// MCP style: caller passes the baseline it last saw. A stale baseline conflicts.
		(await _svc.AppendAsync("proj", "s1", "agent", "v1")).Result.Applied.Should().BeTrue();
		var stale = await _svc.AppendAsync("proj", "s1", "agent", "v2", version: 0);
		stale.Result.Applied.Should().BeFalse();
		stale.Result.HasConflicts.Should().BeTrue();

		// REST Stop-hook style: read the current version first → last-write-wins, no conflict.
		var current = await _svc.GetAsync("proj", "s1");
		var ok = await _svc.AppendAsync("proj", "s1", "agent", "v2", version: current!.Version);
		ok.Result.Applied.Should().BeTrue();
		(await _svc.GetAsync("proj", "s1"))!.Content.Should().Be("v2");
	}
}
