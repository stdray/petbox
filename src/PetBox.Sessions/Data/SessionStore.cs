using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;

namespace PetBox.Sessions.Data;

// Thin accessor over a project's sessions file. No catalog/metadata and no
// container create/delete: a session is created by an agent calling the MCP
// session.upsert tool. The per-project file is materialized on first access
// (the only justified auto-vivify — there is no name to choose, and the file's
// lifecycle == the project's). Read-mostly.
public interface ISessionStore
{
	SessionsDb GetContext(string projectKey);
	Task<IReadOnlyList<SessionRow>> ListAsync(string projectKey, CancellationToken ct = default);
	Task<SessionRow?> GetAsync(string projectKey, string sessionId, CancellationToken ct = default);
}

public sealed class SessionStore : ISessionStore
{
	readonly IScopedDbFactory<SessionsDb> _factory;

	public SessionStore(IScopedDbFactory<SessionsDb> factory)
	{
		_factory = factory;
	}

	public SessionsDb GetContext(string projectKey) =>
		_factory.GetDb(projectKey);

	public async Task<IReadOnlyList<SessionRow>> ListAsync(string projectKey, CancellationToken ct = default)
	{
		var db = _factory.GetDb(projectKey);
		return await db.Sessions
			.Where(s => s.ActiveTo == null)
			.OrderBy(s => s.Key)
			.ToListAsync(ct);
	}

	public async Task<SessionRow?> GetAsync(string projectKey, string sessionId, CancellationToken ct = default)
	{
		var db = _factory.GetDb(projectKey);
		return await db.Sessions
			.Where(s => s.Key == sessionId && s.ActiveTo == null)
			.FirstOrDefaultAsync(ct);
	}
}
