using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Sessions.Contract;

namespace PetBox.Sessions.Data;

// Thin accessor over a project's sessions file. No catalog/metadata and no container
// create/delete: a session is created by an agent push (MCP session_upsert or the REST
// Stop-hook). The per-project file is materialized on first access (the only justified
// auto-vivify — there is no name to choose, and the file's lifecycle == the project's).
public interface ISessionStore
{
	SessionsDb GetContext(string projectKey);
	Task<IReadOnlyList<SessionHeader>> ListAsync(string projectKey, CancellationToken ct = default);
	// Server-paged header slice, optionally filtered by a substring over SessionId/Agent
	// (case-insensitive LIKE). OFFSET/LIMIT at the query — never loads the whole set to page.
	Task<SessionHeaderPage> ListPageAsync(string projectKey, string? search, int pageNum, int pageSize, CancellationToken ct = default);
	Task<SessionSnapshot?> GetAsync(string projectKey, string sessionId, CancellationToken ct = default);
	// Resolve a possibly-shortened id (a unique prefix of a full session id) to the stored full
	// id — see SessionIdResolution. Exact matches win; a prefix that collides with 2+ active
	// sessions is reported ambiguous rather than guessed.
	Task<SessionIdResolution> ResolveIdAsync(string projectKey, string idOrPrefix, CancellationToken ct = default);
	Task UpsertAsync(string projectKey, SessionRow row, CancellationToken ct = default);
	Task<bool> DeleteAsync(string projectKey, string sessionId, CancellationToken ct = default);
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
		using var db = _factory.NewEnsuredConnection(projectKey);
		// Project only the header columns — never load ContentZ blobs just to list.
		var rows = await db.Sessions
			.Where(s => !s.IsDeleted)
			.OrderBy(s => s.SessionId)
			.Select(s => new { s.SessionId, s.Agent, s.Version, s.Updated, s.MetaJson })
			.ToListAsync(ct);
		return rows.Select(r => new SessionHeader(r.SessionId, r.Agent, r.Version, r.Updated, r.MetaJson)).ToList();
	}

	public async Task<SessionHeaderPage> ListPageAsync(string projectKey, string? search, int pageNum, int pageSize, CancellationToken ct = default)
	{
		using var db = _factory.NewEnsuredConnection(projectKey);
		var q = db.Sessions.Where(s => !s.IsDeleted);
		if (!string.IsNullOrWhiteSpace(search))
		{
			var term = search.Trim();
			q = q.Where(s => s.SessionId.Contains(term) || s.Agent.Contains(term));
		}

		var total = await q.CountAsync(ct);
		var offset = Math.Max(0, pageNum) * pageSize;
		// Project only the header columns (never the ContentZ blob); take one extra row as a
		// cheap HasNext probe.
		var rows = await q
			.OrderBy(s => s.SessionId)
			.Skip(offset)
			.Take(pageSize + 1)
			.Select(s => new { s.SessionId, s.Agent, s.Version, s.Updated, s.MetaJson })
			.ToListAsync(ct);

		var hasNext = rows.Count > pageSize;
		if (hasNext) rows.RemoveAt(rows.Count - 1);
		var headers = rows.Select(r => new SessionHeader(r.SessionId, r.Agent, r.Version, r.Updated, r.MetaJson)).ToList();
		return new SessionHeaderPage(headers, hasNext, total);
	}

	public async Task<SessionSnapshot?> GetAsync(string projectKey, string sessionId, CancellationToken ct = default)
	{
		using var db = _factory.NewEnsuredConnection(projectKey);
		var row = await db.Sessions
			.Where(s => s.SessionId == sessionId && !s.IsDeleted)
			.FirstOrDefaultAsync(ct);
		return row is null
			? null
			: new SessionSnapshot(row.SessionId, row.Agent, SessionContent.Decode(row.ContentZ), row.Version, row.Updated, row.MetaJson);
	}

	// How many colliding ids to surface on an ambiguous prefix — enough to show the collision
	// (and let the caller list them), never the whole project.
	const int AmbiguityProbe = 10;

	public async Task<SessionIdResolution> ResolveIdAsync(string projectKey, string idOrPrefix, CancellationToken ct = default)
	{
		var id = (idOrPrefix ?? string.Empty).Trim();
		if (id.Length == 0) return SessionIdResolution.None;

		using var db = _factory.NewEnsuredConnection(projectKey);

		// An exact id always wins — even when it is also a prefix of a longer id — so a full id
		// keeps resolving to itself and never reads as "ambiguous".
		var exact = await db.Sessions
			.Where(s => s.SessionId == id && !s.IsDeleted)
			.Select(s => s.SessionId)
			.FirstOrDefaultAsync(ct);
		if (exact is not null) return SessionIdResolution.One(exact);

		// Otherwise treat the argument as a prefix (StartsWith → LIKE 'id%', hits the SessionId
		// primary-key index). 0 → miss; 1 → resolved; 2+ → ambiguous, surfaced for the caller.
		var matches = await db.Sessions
			.Where(s => !s.IsDeleted && s.SessionId.StartsWith(id))
			.OrderBy(s => s.SessionId)
			.Select(s => s.SessionId)
			.Take(AmbiguityProbe)
			.ToListAsync(ct);

		return matches.Count switch
		{
			0 => SessionIdResolution.None,
			1 => SessionIdResolution.One(matches[0]),
			_ => new SessionIdResolution(null, matches),
		};
	}

	public async Task UpsertAsync(string projectKey, SessionRow row, CancellationToken ct = default)
	{
		using var db = _factory.NewEnsuredConnection(projectKey);
		await db.InsertOrReplaceAsync(row, token: ct);
	}

	public async Task<bool> DeleteAsync(string projectKey, string sessionId, CancellationToken ct = default)
	{
		using var db = _factory.NewEnsuredConnection(projectKey);
		// Idempotent: a second delete (or a miss) updates 0 rows and reports false.
		var rows = await db.Sessions
			.Where(s => s.SessionId == sessionId && !s.IsDeleted)
			.Set(s => s.IsDeleted, true)
			.Set(s => s.DeletedAt, _ => DateTime.UtcNow)
			.UpdateAsync(ct);
		return rows > 0;
	}
}
