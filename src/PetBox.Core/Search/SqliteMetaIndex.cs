using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;

namespace PetBox.Core.Search;

// A facet/alias document for the search META authority — the reference layer (spec
// search-index-authority). Addressed by ENTITY (Scope, Type, Id), exactly like SearchDoc, but
// instead of searchable text it carries the COMPUTED facets a query filters on — a StatusKind
// (open|terminalok|terminalcancel), the temporal Created/Updated — and the identity ALIASES an
// exact/identity lookup resolves through (for a task node: its slug AND its NodeId). The producing
// module computes these at index time from its OWN authority (tasks: MethodologyRuntime.StatusKindOf);
// this record is the neutral carrier and decides no policy.
public readonly record struct SearchMetaDoc(
	string Scope,
	string Type,
	string Id,
	string StatusKind,
	DateTime Created,
	DateTime Updated,
	IReadOnlyList<string> Aliases);

// The Class-A META index: the single authority of index MEMBERSHIP and query FACETS (spec
// search-index-authority). Written INSIDE the entity's transaction (Synchronous consistency), so a
// committed entity's facets are never stale and roll back with it on abort — the SAME transactional
// guarantee SqliteFtsIndex gives the lexical floor, for the SAME reason: one authority, no
// visibility lag, no second copy of a fact. Two tables:
//   search_meta        — one row per entity: the facet columns (StatusKind, Created, Updated).
//   search_meta_alias  — the alias SET, one row per (entity, alias).
// A query JOINS against search_meta (a plain table), NOT search_fts (an FTS5 virtual table a join
// without MATCH would scan). The IDENTITY read leg (ResolveIdentityAsync) lives here too — it is the
// natural owner of the alias-table mapping — while the FACET-pushdown read leg lives with its
// consumer. Writes are the primary surface.
//
// Writes ride the caller's `tx` (Class A): `tx` MUST be non-null. We never open our own write
// connection — that would commit independently of the entity and reintroduce the very lag the
// reference layer exists to remove. STATIC by nature: unlike SqliteFtsIndex (which holds a connect
// func for its read path), the write side carries no state — the entity's transaction is the only
// connection it touches. The identity read (ResolveIdentityAsync) is a pure equality query over the
// alias table and takes a plain read connection; the facet-pushdown read leg is separate work.
public static class SqliteMetaIndex
{
	// Upsert one entity's facet row + alias set: delete any prior row/aliases for (Scope, Type, Id)
	// then insert. Idempotent, same delete-then-insert shape as SqliteFtsIndex — a re-index (a status
	// change, a temporal touch) fully replaces the prior facets and aliases. Runs on the caller's
	// transaction (Class A).
	public static async Task IndexAsync(DataConnection tx, SearchMetaDoc doc, CancellationToken ct = default)
	{
		var db = Tx(tx);
		await DeleteAsync(db, doc.Scope, doc.Type, doc.Id, ct);
		await db.InsertAsync(new SearchMetaRow
		{
			Scope = doc.Scope,
			Type = doc.Type,
			Id = doc.Id,
			StatusKind = doc.StatusKind,
			Created = doc.Created,
			Updated = doc.Updated,
		}, token: ct);
		// Distinct + non-empty: an alias set may legitimately repeat a value (a task node's slug and
		// its Id are the same string) — the alias table's PK is (Scope, Type, Id, Alias), so a dup
		// would violate it. Dedupe here rather than push the burden onto every caller.
		foreach (var alias in doc.Aliases.Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.Ordinal))
			await db.InsertAsync(new AliasRow
			{
				Scope = doc.Scope,
				Type = doc.Type,
				Id = doc.Id,
				Alias = alias,
			}, token: ct);
	}

	// Drop one entity's facet row and its whole alias set (a node left the index / was deleted).
	public static async Task DeleteAsync(DataConnection tx, string scope, string type, string id, CancellationToken ct = default)
	{
		var db = Tx(tx);
		await db.GetTable<SearchMetaRow>().Where(r => r.Scope == scope && r.Type == type && r.Id == id).DeleteAsync(ct);
		await db.GetTable<AliasRow>().Where(r => r.Scope == scope && r.Type == type && r.Id == id).DeleteAsync(ct);
	}

	// Board/type-wide purge: every entity under (scope, type) in one shot — the meta counterpart of
	// SqliteFtsIndex.DeleteByTypeAsync, used when a whole container (a task board) is dropped and its
	// per-id rows would otherwise be orphaned.
	public static async Task DeleteByTypeAsync(DataConnection tx, string scope, string type, CancellationToken ct = default)
	{
		var db = Tx(tx);
		await db.GetTable<SearchMetaRow>().Where(r => r.Scope == scope && r.Type == type).DeleteAsync(ct);
		await db.GetTable<AliasRow>().Where(r => r.Scope == scope && r.Type == type).DeleteAsync(ct);
	}

	// ---- read leg: identity resolution ----
	//
	// The identity leg of the search pipeline (spec search-identity-leg): resolve an identifier to the
	// entities whose ALIAS SET contains it exactly. A plain EQUALITY predicate over search_meta_alias
	// (`Alias = @alias`) — NOT a temporal-store read and NOT an FTS5 MATCH — so index MEMBERSHIP is the
	// single truth about findability by identifier. Because the alias set carries a task node's slug AND
	// its NodeId, ONE predicate resolves both: a NodeId query and a slug query land the same rank-1
	// identity hit (slug↔NodeId symmetry). Cross-scope isolation: `Scope = @scope` is always applied, so
	// a resolution never crosses into another project's rows. `type` narrows to one container (a board)
	// when given. Returns the matched (Type, Id) = (board, slug) addresses, deduplicated and ordered by
	// (Type, Id) for a stable multi-board result; the caller enriches each to a node view. This is a
	// READ (no `tx` required) — it never writes, so it takes a plain connection.
	public static async Task<IReadOnlyList<(string Type, string Id)>> ResolveIdentityAsync(
		DataConnection db, string scope, string alias, string? type = null, CancellationToken ct = default)
	{
		if (string.IsNullOrEmpty(alias)) return [];
		var q = db.GetTable<AliasRow>().Where(r => r.Scope == scope && r.Alias == alias);
		if (type is not null) q = q.Where(r => r.Type == type);
		return (await q.Select(r => new { r.Type, r.Id }).ToListAsync(ct))
			.Select(r => (r.Type, r.Id))
			.Distinct()
			.OrderBy(x => x.Type, StringComparer.Ordinal)
			.ThenBy(x => x.Id, StringComparer.Ordinal)
			.ToList();
	}

	// ---- read leg: facet lookup (listing parity) ----
	//
	// The stored StatusKind facet for every entity under (scope, type), as Id → StatusKind (spec
	// tasks-listing-search-predicate-parity). A listing evaluates its statusKind predicate against
	// THIS — the SAME опорный слой the query legs' facet pushdown joins — so listing and search share
	// ONE authority and there is NO live classifier recompute on read. A plain equality read over the
	// search_meta table (no `tx`); the caller filters its hydrated rows by the returned map.
	public static async Task<Dictionary<string, string>> StatusKindsByTypeAsync(
		DataConnection db, string scope, string type, CancellationToken ct = default) =>
		(await db.GetTable<SearchMetaRow>()
			.Where(r => r.Scope == scope && r.Type == type)
			.Select(r => new { r.Id, r.StatusKind })
			.ToListAsync(ct))
		.ToDictionary(r => r.Id, r => r.StatusKind, StringComparer.Ordinal);

	static DataConnection Tx(DataConnection? tx) =>
		tx ?? throw new InvalidOperationException("SqliteMetaIndex is Class-A (transactional): a write must ride the entity's transaction (tx).");

	[Table("search_meta_alias")]
	sealed class AliasRow
	{
		[Column, PrimaryKey(0)] public string Scope { get; set; } = string.Empty;
		[Column, PrimaryKey(1)] public string Type { get; set; } = string.Empty;
		[Column, PrimaryKey(2)] public string Id { get; set; } = string.Empty;
		[Column, PrimaryKey(3)] public string Alias { get; set; } = string.Empty;
	}
}

// The search_meta facet row, shared across the Core.Search assembly: SqliteMetaIndex owns the WRITE
// side, and the two read legs (SqliteFtsIndex, VectorSearchIndex) join against it for the FACET
// pushdown (spec search-facet-pushdown). One row per indexed entity, addressed (Scope, Type, Id) —
// the SAME address the text/vector rows carry — so a leg joins its candidate to this by primary key.
[Table("search_meta")]
sealed class SearchMetaRow
{
	[Column, PrimaryKey(0)] public string Scope { get; set; } = string.Empty;
	[Column, PrimaryKey(1)] public string Type { get; set; } = string.Empty;
	[Column, PrimaryKey(2)] public string Id { get; set; } = string.Empty;
	[Column] public string StatusKind { get; set; } = string.Empty;
	[Column] public DateTime Created { get; set; }
	[Column] public DateTime Updated { get; set; }
}
