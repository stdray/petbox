using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.Mapping;

namespace PetBox.Core.Search;

// The trivial Class-A index that gives the contract an end-to-end pass: a local SQLite
// FTS5 mirror, written INSIDE the entity's transaction (Synchronous consistency) so a
// committed entity is never lexically-stale, and rolled back with it on abort
// (spec: search-lexical-floor). Entity-addressed by (Scope, Type, Id); Text/Tags/Key/Title are the
// indexed columns (Key: search-key-column-everywhere — the entity's own business key/slug,
// tokenized in its OWN column instead of prose; Title: search-doc-model-title-weights — the
// entity's title in its OWN column so a title hit can be weighted above a body hit, instead of
// being spliced into `Text` where bm25 could not tell them apart). Tokenizer fix for ru/en lives
// in the shared FtsQuery helper. MATCH with no column filter searches every indexed column
// (Text, Tags, Key, Title) as one term space, so a key-word or title-word query is found with zero
// query-side change; the column WEIGHTS (FtsColumnWeights) then rank a title/key hit above a body
// hit within that same matched set.
//
// Reads open their own connection via the supplied factory; writes ride the caller's `tx`.
// This is the floor, not the ceiling — vector/episodic indexes are separate Class-B work.
public sealed class SqliteFtsIndex : ISearchIndex
{
	readonly Func<DataConnection> _connect;

	// connect: opens a fresh DataConnection to the SAME SQLite file the entity lives in
	// (used for reads; writes use the transaction the caller passes to IndexAsync/DeleteAsync).
	public SqliteFtsIndex(Func<DataConnection> connect) => _connect = connect;

	public SearchConsistency ConsistencyClass => SearchConsistency.Synchronous;
	public SearchCapability Capability => SearchCapability.Lexical;
	// ENUMERABLE (spec: search-leg-classification): an FTS MATCH is a boolean predicate, so this
	// leg can return its ENTIRE matched set. It does — the read below carries NO top-K truncation.
	public SearchLegClass LegClass => SearchLegClass.Enumerable;

	// No EnsureSchema here: the search_fts virtual table is DDL, and DDL is born in exactly one
	// place — the owning module's migration (memory M006_SearchTables, tasks M009_SearchTables).
	// The index just assumes the table its file's migration set created.

	// Upsert one entity's row: delete any prior row for (Scope, Type, Id) then insert.
	// Runs on the caller's transaction (Class A) — `tx` must be non-null for a Synchronous
	// index. We never open our own write connection here: that would break the transactional
	// guarantee (a separate connection commits independently of the entity).
	public async Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default)
	{
		var db = Tx(tx);
		await db.GetTable<Row>()
			.Where(r => r.Scope == doc.Scope && r.Type == doc.Type && r.Id == doc.Id)
			.DeleteAsync(ct);
		// The indexed prose carries the tokens' snowball stems as shadow terms, so a
		// stemmed query token lands on any wordform (spec: search-lexical-multilingual).
		// Appending keeps the fts5 schema untouched; neither column is read back (hits carry
		// only the entity address), so the shadow can't leak into display. Title is prose in its
		// OWN column now, so it gets the SAME stemming shadow the body does — a stemmed query token
		// must reach a title wordform too, not only a body one.
		await db.InsertAsync(new Row
		{
			Scope = doc.Scope,
			Type = doc.Type,
			Id = doc.Id,
			Text = WithShadow(doc.Text),
			Tags = doc.Tags ?? string.Empty,
			Key = doc.Key,
			Title = WithShadow(doc.Title),
		}, token: ct);
	}

	public Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default) =>
		Tx(tx).GetTable<Row>()
			.Where(r => r.Scope == scope && r.Type == type && r.Id == id)
			.DeleteAsync(ct);

	public Task DeleteByTypeAsync(DataConnection? tx, string scope, string type, CancellationToken ct = default) =>
		Tx(tx).GetTable<Row>()
			.Where(r => r.Scope == scope && r.Type == type)
			.DeleteAsync(ct);

	public async Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default)
	{
		var match = FtsQuery.BuildMatch(query);
		if (match is null) return [];

		using var db = _connect();
		var q = db.GetTable<Row>()
			.Where(r => r.Scope == scope && Sql.Ext.SQLite().Match(r, match));
		if (filter.Type is not null) q = q.Where(r => r.Type == filter.Type);
		// Include-SET narrowing (translates to `Type IN (...)`) — one query over several
		// containers sharing this index, instead of one query per container.
		if (filter.Types is { Count: > 0 } types) q = q.Where(r => types.Contains(r.Type));

		// Facet pushdown (spec search-facet-pushdown): drop candidates a facet predicate excludes
		// BEFORE the Take(k) below, joined to the search_meta reference layer by the entity address
		// (Scope, Type, Id = search_meta's primary key) — a correlated NOT EXISTS so it stays one
		// SQL query and the join is a per-row PK seek. StatusKind is a residual filter over the tiny
		// MATCH result, not a seek key, so no index on it is needed. An entity with NO meta row (a
		// comment doc) has no matching `m`, so the NOT EXISTS keeps it — a facet it does not carry
		// cannot hide it. Neutral when no facet is set (memory passes none → no join emitted).
		if (filter.Facets is { ExcludeStatusKinds: { Count: > 0 } excluded })
		{
			string[] kinds = [.. excluded];
			q = q.Where(r => !db.GetTable<SearchMetaRow>()
				.Any(m => m.Scope == scope && m.Type == r.Type && m.Id == r.Id && kinds.Contains(m.StatusKind)));
		}

		// FTS5 COLUMN-WEIGHTED bm25 (spec: search-doc-model-title-weights): more-negative = more
		// relevant. The per-column weights (FtsColumnWeights) rank a Key/Title hit above a Body hit
		// within the matched set — one default across every family, positional over the declared
		// columns. Order ascending (best first), and surface a higher-is-better score for honest
		// provenance. NO Take(k): an enumerable leg returns its FULL matched set
		// (search-leg-classification) — the facade truncates the fused pool for a relevance
		// selection, and a scan selection needs every match, so truncating here would silently cap
		// the enumerable set. `k` is the caller's fusion-pool hint, not a cap on this leg's
		// membership — so it is intentionally not applied here.
		// The bm25 weight arguments are POSITIONAL over the search_fts columns in DECLARED order —
		// [Scope, Type, Id, Text, Tags, Key, Title] — the UNINDEXED address columns included (they
		// score nothing but still occupy positions 0..2). The VALUES live once in FtsColumnWeights;
		// they are `const` so the compiler inlines them as the literals fts5's bm25() spread requires.
		var rows = await q
			.OrderBy(r => Sql.Ext.SQLite().FTS5bm25(r,
				FtsColumnWeights.Unindexed, FtsColumnWeights.Unindexed, FtsColumnWeights.Unindexed,
				FtsColumnWeights.Body, FtsColumnWeights.Tags, FtsColumnWeights.Key, FtsColumnWeights.Title))
			.Select(r => new
			{
				r.Type,
				r.Id,
				Rank = Sql.Ext.SQLite().FTS5bm25(r,
				FtsColumnWeights.Unindexed, FtsColumnWeights.Unindexed, FtsColumnWeights.Unindexed,
				FtsColumnWeights.Body, FtsColumnWeights.Tags, FtsColumnWeights.Key, FtsColumnWeights.Title)
			})
			.ToListAsync(ct);

		return rows.Select(r => new Hit(r.Type, r.Id, -r.Rank, "lexical")).ToList();
	}

	static DataConnection Tx(DataConnection? tx) =>
		tx ?? throw new InvalidOperationException("SqliteFtsIndex is Class-A (transactional): a write must ride the entity's transaction (tx).");

	// Append the snowball-stem shadow terms to a prose field (empty in → empty out).
	static string WithShadow(string prose)
	{
		if (string.IsNullOrEmpty(prose)) return prose ?? string.Empty;
		var shadow = TokenStemmer.ShadowTerms(prose);
		return shadow.Length == 0 ? prose : prose + "\n" + shadow;
	}

	[Table("search_fts")]
	sealed class Row
	{
		[Column] public string Scope { get; set; } = string.Empty;
		[Column] public string Type { get; set; } = string.Empty;
		[Column] public string Id { get; set; } = string.Empty;
		[Column] public string Text { get; set; } = string.Empty;
		[Column] public string Tags { get; set; } = string.Empty;
		[Column] public string Key { get; set; } = string.Empty;
		[Column] public string Title { get; set; } = string.Empty;
	}
}
