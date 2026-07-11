using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.Mapping;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Core.Search;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;

namespace PetBox.Sessions.Search;

// Default ISessionTermIndex: one fts5 row per session (session_term_fts) holding the raw
// content plus its snowball shadow terms (TokenStemmer.ShadowTerms — the SAME analyzer the
// episodic tier's DuckDB stemmer='russian' and the Class-A SqliteFtsIndex agree on, so a
// query stem lands on this index too), and a plain cursor table (session_term_cursor)
// tracking the last indexed Version per session. DDL: M005_SessionTermIndex.
//
// Population is chat-free and runs off the SAME background tick as digest distillation
// (registered as its own IBackgroundIndexJob in PetBox.Web — SessionTermIndexJob) but does
// NOT share the digest's quiet-period/LLM gates: a plain tokenization pass has no reason to
// wait for an idle session or an available chat model, so old sessions backfill on the very
// first pass (their cursor starts at 0, below any real Version).
public sealed class SessionTermIndex : ISessionTermIndex
{
	readonly IScopedDbFactory<SessionsDb> _factory;
	readonly IProjectCatalog _catalog;
	readonly ISessionService _sessions;
	readonly ILogger<SessionTermIndex>? _logger;

	public SessionTermIndex(IScopedDbFactory<SessionsDb> factory, IProjectCatalog catalog,
		ISessionService sessions, ILogger<SessionTermIndex>? logger = null)
	{
		_factory = factory;
		_catalog = catalog;
		_sessions = sessions;
		_logger = logger;
	}

	public async Task<IReadOnlyList<string>> SearchAsync(string projectKey, string query, int k, CancellationToken ct = default)
	{
		var match = FtsQuery.BuildMatch(query);
		if (match is null) return [];

		using var db = _factory.NewEnsuredConnection(projectKey);
		var rows = await db.GetTable<FtsRow>()
			.Where(r => Sql.Ext.SQLite().Match(r, match))
			.OrderBy(r => Sql.Ext.SQLite().Rank(r))
			.Take(k)
			.Select(r => r.SessionId)
			.ToListAsync(ct);
		return rows;
	}

	public async Task<int> DrainAllAsync(CancellationToken ct = default)
	{
		var indexed = 0;
		// Catalog, not file scan (spec: catalog-is-source-of-truth): the pass walks the PROJECT
		// catalog (core.db), not sessions/*.db. Sessions have no per-entity catalog, so the project
		// list IS the work list — a project whose file has not been materialized yet is no longer
		// invisible, and a deleted project's ghost file is no longer indexed.
		//
		// Lazy-creation: this job is chat-free and has NO gate before the store, so it opens (→
		// creates + migrates) sessions/{project}.db for every catalog project. Deliberate, and the
		// cheapest place in the system to pay for it: the ensure runs once per project per process
		// (the ScopedDbFactory is a singleton and memoizes the ensure flag), after which a tick costs
		// one SQLite open plus a cursor read against an empty table. The alternative — a
		// File.Exists() short-circuit — is precisely the bug this card removes: it makes the
		// filesystem the source of truth again.
		foreach (var project in await _catalog.ListProjectKeysAsync(ct))
		{
			ct.ThrowIfCancellationRequested();
			try
			{
				using var db = _factory.NewEnsuredConnection(project);
				var headers = await _sessions.ListAsync(project, ct);
				if (headers.Count == 0) continue;

				var cursors = await db.GetTable<CursorRow>().ToDictionaryAsync(r => r.SessionId, r => r.Cursor, ct);
				foreach (var header in headers)
				{
					ct.ThrowIfCancellationRequested();
					cursors.TryGetValue(header.SessionId, out var cursor);
					if (header.Version <= cursor) continue; // unchanged since the last pass

					var snap = await _sessions.GetAsync(project, header.SessionId, ct);
					if (snap is null) continue; // deleted between the header list and here

					await UpsertRowAsync(db, header.SessionId, snap.Content, header.Version, ct);
					indexed++;
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				_logger?.LogError(ex, "session term-index pass failed for project {Project}; skipped", project);
			}
		}
		return indexed;
	}

	static async Task UpsertRowAsync(SessionsDb db, string sessionId, string content, long version, CancellationToken ct)
	{
		await db.GetTable<FtsRow>().Where(r => r.SessionId == sessionId).DeleteAsync(ct);
		// Shadow stems appended exactly like SqliteFtsIndex: a stemmed query token (FtsQuery
		// widens to `(tok* OR stem*)`) lands on the shadow text even when the raw wordform
		// in `content` differs.
		var shadow = TokenStemmer.ShadowTerms(content);
		await db.InsertAsync(new FtsRow
		{
			SessionId = sessionId,
			Text = shadow.Length == 0 ? content : content + "\n" + shadow,
		}, token: ct);
		await db.InsertOrReplaceAsync(new CursorRow { SessionId = sessionId, Cursor = version }, token: ct);
	}

	[Table("session_term_fts")]
	sealed class FtsRow
	{
		[Column] public string SessionId { get; set; } = string.Empty;
		[Column] public string Text { get; set; } = string.Empty;
	}

	[Table("session_term_cursor")]
	sealed class CursorRow
	{
		[Column, PrimaryKey] public string SessionId { get; set; } = string.Empty;
		[Column] public long Cursor { get; set; }
	}
}
