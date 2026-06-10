using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.Mapping;

namespace PetBox.Core.Search;

// The trivial Class-A index that gives the contract an end-to-end pass: a local SQLite
// FTS5 mirror, written INSIDE the entity's transaction (Synchronous consistency) so a
// committed entity is never lexically-stale, and rolled back with it on abort
// (spec: search-lexical-floor). Entity-addressed by (Scope, Type, Id); Text/Tags are the
// indexed columns. Tokenizer fix for ru/en lives in the shared FtsQuery helper.
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

	// Idempotent DDL. Called once during the store's schema bootstrap; the FTS5 virtual
	// table must exist in the entity's file so writes can join its transaction.
	public static void EnsureSchema(DataConnection db) => db.Execute("""
		CREATE VIRTUAL TABLE IF NOT EXISTS search_fts USING fts5(
			Scope UNINDEXED, Type UNINDEXED, Id UNINDEXED, Text, Tags, tokenize='unicode61'
		);
		""");

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
		// The indexed text carries the tokens' snowball stems as shadow terms, so a
		// stemmed query token lands on any wordform (spec: search-lexical-multilingual).
		// Appending keeps the fts5 schema untouched; Text is never read back (hits carry
		// only the entity address), so the shadow can't leak into display.
		var shadow = TokenStemmer.ShadowTerms(doc.Text);
		await db.InsertAsync(new Row
		{
			Scope = doc.Scope,
			Type = doc.Type,
			Id = doc.Id,
			Text = shadow.Length == 0 ? doc.Text : doc.Text + "\n" + shadow,
			Tags = doc.Tags ?? string.Empty,
		}, token: ct);
	}

	public Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default) =>
		Tx(tx).GetTable<Row>()
			.Where(r => r.Scope == scope && r.Type == type && r.Id == id)
			.DeleteAsync(ct);

	public async Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default)
	{
		var match = FtsQuery.BuildMatch(query);
		if (match is null) return [];

		using var db = _connect();
		var q = db.GetTable<Row>()
			.Where(r => r.Scope == scope && Sql.Ext.SQLite().Match(r, match));
		if (filter.Type is not null) q = q.Where(r => r.Type == filter.Type);

		// FTS5 bm25 rank: more-negative = more relevant. Order ascending (best first), and
		// surface a higher-is-better score for honest provenance.
		var rows = await q
			.OrderBy(r => Sql.Ext.SQLite().Rank(r))
			.Take(k)
			.Select(r => new { r.Type, r.Id, Rank = Sql.Ext.SQLite().Rank(r) })
			.ToListAsync(ct);

		return rows.Select(r => new Hit(r.Type, r.Id, -(r.Rank ?? 0d), "lexical")).ToList();
	}

	static DataConnection Tx(DataConnection? tx) =>
		tx ?? throw new InvalidOperationException("SqliteFtsIndex is Class-A (transactional): a write must ride the entity's transaction (tx).");

	[Table("search_fts")]
	sealed class Row
	{
		[Column] public string Scope { get; set; } = string.Empty;
		[Column] public string Type { get; set; } = string.Empty;
		[Column] public string Id { get; set; } = string.Empty;
		[Column] public string Text { get; set; } = string.Empty;
		[Column] public string Tags { get; set; } = string.Empty;
	}
}
