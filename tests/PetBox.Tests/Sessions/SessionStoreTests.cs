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
