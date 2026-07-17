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
// without MATCH would scan) — but that read leg (facet pushdown, identity resolution) lives with its
// consumers. This class is the WRITE side only.
//
// Writes ride the caller's `tx` (Class A): `tx` MUST be non-null. We never open our own write
// connection — that would commit independently of the entity and reintroduce the very lag the
// reference layer exists to remove. STATIC by nature: unlike SqliteFtsIndex (which holds a connect
// func for its read path), the write side carries no state — the entity's transaction is the only
// connection it touches. The read legs (facet pushdown, identity resolution) are separate work.
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
		await db.InsertAsync(new MetaRow
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
		await db.GetTable<MetaRow>().Where(r => r.Scope == scope && r.Type == type && r.Id == id).DeleteAsync(ct);
		await db.GetTable<AliasRow>().Where(r => r.Scope == scope && r.Type == type && r.Id == id).DeleteAsync(ct);
	}

	// Board/type-wide purge: every entity under (scope, type) in one shot — the meta counterpart of
	// SqliteFtsIndex.DeleteByTypeAsync, used when a whole container (a task board) is dropped and its
	// per-id rows would otherwise be orphaned.
	public static async Task DeleteByTypeAsync(DataConnection tx, string scope, string type, CancellationToken ct = default)
	{
		var db = Tx(tx);
		await db.GetTable<MetaRow>().Where(r => r.Scope == scope && r.Type == type).DeleteAsync(ct);
		await db.GetTable<AliasRow>().Where(r => r.Scope == scope && r.Type == type).DeleteAsync(ct);
	}

	static DataConnection Tx(DataConnection? tx) =>
		tx ?? throw new InvalidOperationException("SqliteMetaIndex is Class-A (transactional): a write must ride the entity's transaction (tx).");

	[Table("search_meta")]
	sealed class MetaRow
	{
		[Column, PrimaryKey(0)] public string Scope { get; set; } = string.Empty;
		[Column, PrimaryKey(1)] public string Type { get; set; } = string.Empty;
		[Column, PrimaryKey(2)] public string Id { get; set; } = string.Empty;
		[Column] public string StatusKind { get; set; } = string.Empty;
		[Column] public DateTime Created { get; set; }
		[Column] public DateTime Updated { get; set; }
	}

	[Table("search_meta_alias")]
	sealed class AliasRow
	{
		[Column, PrimaryKey(0)] public string Scope { get; set; } = string.Empty;
		[Column, PrimaryKey(1)] public string Type { get; set; } = string.Empty;
		[Column, PrimaryKey(2)] public string Id { get; set; } = string.Empty;
		[Column, PrimaryKey(3)] public string Alias { get; set; } = string.Empty;
	}
}
