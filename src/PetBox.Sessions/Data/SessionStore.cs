using System.Globalization;
using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Contract;
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
	// KEYSET-paged header slice (spec listing-tail-reachable; card
	// listing-keyset-memory-sessions), optionally filtered by a substring over SessionId/Agent
	// (case-insensitive LIKE) AND/OR an exact `agent` match (the UI's agent-filter dropdown —
	// a distinct predicate from the free-text `search` box, combinable with it). `sort` is a
	// real SQL ORDER BY, SessionId always the secondary key (Updated/Created/Version — the
	// length proxy — are none of them unique, so without this tiebreak the cursor boundary
	// would be unstable). `cursor` is the opaque token from a PREVIOUS page's NextCursor — a
	// token issued for a different search/agent/sort/direction is a thrown ArgumentException
	// (KeysetCursor's own strict-decode contract), never a silent restart. Never OFFSETs:
	// an offset is wrong under concurrent writes (a row inserted/deleted before the boundary
	// silently re-serves/swallows a row) — this fetches the full filtered+sorted HEADER set
	// (no ContentZ blobs, so still cheap) and seeks past the cursor in memory
	// (KeysetCursor.Advance), mirroring tasks_search's listing mode.
	Task<SessionHeaderPage> ListPageAsync(string projectKey, string? search, string? agent,
		SessionSortField sort, bool sortDesc, string? cursor, int pageSize, CancellationToken ct = default);
	Task<SessionSnapshot?> GetAsync(string projectKey, string sessionId, CancellationToken ct = default);
	// Cheap lookup: the row's Created timestamp ONLY (no content decode) — no IsDeleted filter,
	// so Created survives a soft-delete/resurrect cycle. Used by SessionService to preserve a
	// session's original first-seen time across every re-push (UpsertAsync/AppendAsync always
	// rewrite the whole row, so this is the only way to know it already existed). Null = brand
	// new session (never written before).
	Task<DateTime?> GetCreatedAsync(string projectKey, string sessionId, CancellationToken ct = default);
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
			.Select(s => new { s.SessionId, s.Agent, s.Version, s.Updated, s.MetaJson, s.Created })
			.ToListAsync(ct);
		return rows.Select(r => new SessionHeader(r.SessionId, r.Agent, r.Version, r.Updated, r.MetaJson, r.Created)).ToList();
	}

	public async Task<SessionHeaderPage> ListPageAsync(string projectKey, string? search, string? agent,
		SessionSortField sort, bool sortDesc, string? cursor, int pageSize, CancellationToken ct = default)
	{
		using var db = _factory.NewEnsuredConnection(projectKey);
		var q = db.Sessions.Where(s => !s.IsDeleted);
		if (!string.IsNullOrWhiteSpace(search))
		{
			var term = search.Trim();
			q = q.Where(s => s.SessionId.Contains(term) || s.Agent.Contains(term));
		}
		if (!string.IsNullOrWhiteSpace(agent))
		{
			var a = agent.Trim();
			q = q.Where(s => s.Agent == a);
		}

		// Fingerprint: everything that decides WHICH rows are selected and in WHAT order —
		// search/agent/sort/direction (spec listing-tail-reachable). pageSize is deliberately
		// excluded: it shapes the PAGE, not the sequence, so a caller may vary it between pages
		// (mirrors tasks_search's SearchFingerprint).
		var fingerprint = KeysetCursor.FingerprintOf(
			"sessions", projectKey, search?.Trim(), agent?.Trim(), sort.ToString(), sortDesc ? "1" : "0");

		// A real SQL ORDER BY over the header columns only (never ContentZ) — SessionId is
		// ALWAYS the secondary key: Updated/Created can tie (same-second writes) and Version
		// (the length proxy) is far from unique, so without this tiebreak the keyset boundary
		// would be unstable (spec listing-tail-reachable: the cursor addresses a row inside a
		// TOTAL order, not just a sort-value bucket).
		var ordered = sort switch
		{
			SessionSortField.Created => sortDesc ? q.OrderByDescending(s => s.Created) : q.OrderBy(s => s.Created),
			SessionSortField.Length => sortDesc ? q.OrderByDescending(s => s.Version) : q.OrderBy(s => s.Version),
			_ => sortDesc ? q.OrderByDescending(s => s.Updated) : q.OrderBy(s => s.Updated),
		};
		var rows = await ordered.ThenBy(s => s.SessionId)
			.Select(s => new { s.SessionId, s.Agent, s.Version, s.Updated, s.MetaJson, s.Created })
			.ToListAsync(ct);
		var headers = rows.Select(r => new SessionHeader(r.SessionId, r.Agent, r.Version, r.Updated, r.MetaJson, r.Created)).ToList();

		// Seek past the cursor (KeysetCursor.Advance — the SAME primitive tasks_search's
		// listing mode uses): a foreign/stale token (a different search/agent/sort/direction)
		// throws rather than silently restarting the walk under a different ordering.
		IReadOnlyList<SessionHeader> afterCursor = headers;
		if (!string.IsNullOrWhiteSpace(cursor))
		{
			var decoded = KeysetCursor.Decode(cursor, fingerprint, "sessions listing");
			afterCursor = KeysetCursor.Advance(
				headers, decoded,
				h => (CursorSortValue(h, sort), h.SessionId, "sessions"),
				CursorSortComparison(sort), sortDesc, "sessions listing");
		}

		var hasMore = afterCursor.Count > pageSize;
		var page = hasMore ? afterCursor.Take(pageSize).ToList() : afterCursor.ToList();
		var nextCursor = hasMore
			? new KeysetCursor(fingerprint, CursorSortValue(page[^1], sort), page[^1].SessionId, "sessions").Encode()
			: null;
		return new SessionHeaderPage(page, nextCursor);
	}

	// The canonical sort-key value of one header, as the string a cursor carries — mirrors
	// TasksTools.CursorSortValue's shape for the same reason (a keyset token needs a
	// STRING the store can also re-parse to find the boundary; see CursorSortComparison).
	static string CursorSortValue(SessionHeader h, SessionSortField sort) => sort switch
	{
		SessionSortField.Created => h.Created.ToString("O", CultureInfo.InvariantCulture),
		SessionSortField.Length => h.Version.ToString(CultureInfo.InvariantCulture),
		_ => h.Updated.ToString("O", CultureInfo.InvariantCulture),
	};

	// How two of those canonical values compare — Length numerically, the timestamps as
	// instants (never as text, where "2026-07-02" would sort after "2026-07-10").
	static Comparison<string> CursorSortComparison(SessionSortField sort) => sort switch
	{
		SessionSortField.Length => static (a, b) =>
			long.Parse(a, CultureInfo.InvariantCulture).CompareTo(long.Parse(b, CultureInfo.InvariantCulture)),
		_ => static (a, b) => DateTime.Parse(a, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
			.CompareTo(DateTime.Parse(b, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)),
	};

	public async Task<SessionSnapshot?> GetAsync(string projectKey, string sessionId, CancellationToken ct = default)
	{
		using var db = _factory.NewEnsuredConnection(projectKey);
		var row = await db.Sessions
			.Where(s => s.SessionId == sessionId && !s.IsDeleted)
			.FirstOrDefaultAsync(ct);
		return row is null
			? null
			: new SessionSnapshot(row.SessionId, row.Agent, SessionContent.Decode(row.ContentZ), row.Version, row.Updated, row.MetaJson, row.Created);
	}

	public async Task<DateTime?> GetCreatedAsync(string projectKey, string sessionId, CancellationToken ct = default)
	{
		using var db = _factory.NewEnsuredConnection(projectKey);
		// No IsDeleted filter — a resurrected session must keep its ORIGINAL Created, not a new
		// one from the re-push that resurrects it.
		var rows = await db.Sessions
			.Where(s => s.SessionId == sessionId)
			.Select(s => (DateTime?)s.Created)
			.ToListAsync(ct);
		return rows.Count > 0 ? rows[0] : null;
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
