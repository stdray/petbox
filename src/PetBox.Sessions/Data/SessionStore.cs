using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Sessions.Contract;

namespace PetBox.Sessions.Data;

// Thin accessor over a project's sessions file. No catalog/metadata and no container
// create/delete: a session is created by an agent push (MCP session.upsert or the REST
// Stop-hook). The per-project file is materialized on first access (the only justified
// auto-vivify — there is no name to choose, and the file's lifecycle == the project's).
public interface ISessionStore
{
	SessionsDb GetContext(string projectKey);
	Task<IReadOnlyList<SessionHeader>> ListAsync(string projectKey, CancellationToken ct = default);
	Task<SessionSnapshot?> GetAsync(string projectKey, string sessionId, CancellationToken ct = default);
	Task UpsertAsync(string projectKey, SessionRow row, CancellationToken ct = default);
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

	public async Task<IReadOnlyList<SessionHeader>> ListAsync(string projectKey, CancellationToken ct = default)
	{
		var db = _factory.GetDb(projectKey);
		// Project only the header columns — never load ContentZ blobs just to list.
		var rows = await db.Sessions
			.OrderBy(s => s.SessionId)
			.Select(s => new { s.SessionId, s.Agent, s.Version, s.Updated })
			.ToListAsync(ct);
		return rows.Select(r => new SessionHeader(r.SessionId, r.Agent, r.Version, r.Updated)).ToList();
	}

	public async Task<SessionSnapshot?> GetAsync(string projectKey, string sessionId, CancellationToken ct = default)
	{
		var db = _factory.GetDb(projectKey);
		var row = await db.Sessions
			.Where(s => s.SessionId == sessionId)
			.FirstOrDefaultAsync(ct);
		return row is null
			? null
			: new SessionSnapshot(row.SessionId, row.Agent, SessionContent.Decode(row.ContentZ), row.Version, row.Updated);
	}

	public async Task UpsertAsync(string projectKey, SessionRow row, CancellationToken ct = default)
	{
		var db = _factory.GetDb(projectKey);
		await db.InsertOrReplaceAsync(row, token: ct);
	}
}
