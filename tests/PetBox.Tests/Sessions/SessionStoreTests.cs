using LinqToDB;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Settings;
using PetBox.Sessions.Data;

namespace PetBox.Tests.Sessions;

[Collection("DataModule")]
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
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	[Fact]
	public void GetContext_AutoVivifies_ProjectFile()
	{
		_ = _store.GetContext("proj");
		File.Exists(ScopedDbFiles.PathFor(_factory.BaseDir, "proj", null)).Should().BeTrue();
	}

	[Fact]
	public async Task Append_Then_Get_And_List()
	{
		var ctx = _store.GetContext("proj");
		var r = await TemporalStore.UpsertAsync(ctx, new[]
		{
			new SessionRow { Key = "s1", Version = 0, Agent = "claude-code", Content = "# plan v1" },
		});
		r.Applied.Should().BeTrue();

		(await _store.GetAsync("proj", "s1"))!.Content.Should().Be("# plan v1");
		(await _store.ListAsync("proj")).Select(s => s.Key).Should().Equal("s1");
	}
}
