using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;

namespace PetBox.Core.Search;

// The first real Class-B index: semantic search by embedding similarity. Eventual consistency —
// it is materialized OFF the entity write path (by the async-vectorization worker, not the
// facade), so a slow/down embedder never blocks or fails a write. Brute-force cosine: at our
// scale an ANN index is slower and heavier than BLAS brute-force (perf verdict
// m-5be78b78/zvec-rejected).
//
// `dim` is the MRL (Matryoshka) truncation target: a Matryoshka embedding's leading `dim`
// components are themselves a valid lower-res embedding, so truncating trades recall for
// RAM/storage WITHOUT a model swap. Default 1024 — the LoCoMo dim-sweep (m-981885fb) showed 256
// over-truncates the 2560-dim qwen3-embed-4b (semantic recall@5 0.66→0.75 at 1024) while 2560
// doesn't beat 1024; tune down only under RAM pressure, accepting the recall hit.
//
// Adding this index to a SearchService makes reads hybrid for free: the facade already RRF-fuses
// every index's ranked list and lifts Vector capability to semantic=true provenance.
public sealed class VectorSearchIndex : ISearchIndex
{
	const char Sep = '\x1f';

	readonly Func<DataConnection> _connect;
	readonly IEmbedder _embedder;
	readonly int _dim; // MRL target dim; <= 0 or >= model dim keeps the full vector

	public VectorSearchIndex(Func<DataConnection> connect, IEmbedder embedder, int dim = 1024)
	{
		_connect = connect;
		_embedder = embedder;
		_dim = dim;
	}

	public SearchConsistency ConsistencyClass => SearchConsistency.Eventual;
	public SearchCapability Capability => SearchCapability.Vector;

	public static void EnsureSchema(DataConnection db) => db.Execute("""
		CREATE TABLE IF NOT EXISTS search_vec (
			Scope TEXT NOT NULL, Type TEXT NOT NULL, Id TEXT NOT NULL,
			Model TEXT NOT NULL, Dim INTEGER NOT NULL, Vec BLOB NOT NULL,
			PRIMARY KEY (Scope, Type, Id)
		);
		""");

	// Eventual: the worker calls this, not the entity transaction. `tx` is honoured when the
	// caller already holds a connection to this file; otherwise we open (and dispose) our own.
	// Embedding is network I/O — it MUST stay off any entity transaction, which is exactly why
	// this index is Class-B.
	public async Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default)
	{
		var batch = await _embedder.EmbedAsync([doc.Text], ct);
		var vec = Truncate(batch.Vectors[0], _dim);

		var (db, own) = Open(tx);
		try
		{
			await db.GetTable<Row>()
				.Where(r => r.Scope == doc.Scope && r.Type == doc.Type && r.Id == doc.Id)
				.DeleteAsync(ct);
			await db.InsertAsync(new Row
			{
				Scope = doc.Scope,
				Type = doc.Type,
				Id = doc.Id,
				Model = batch.Model,
				Dim = vec.Length,
				Vec = VectorCodec.Encode(vec),
			}, token: ct);
		}
		finally { if (own) db.Dispose(); }
	}

	public async Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default)
	{
		var (db, own) = Open(tx);
		try
		{
			await db.GetTable<Row>()
				.Where(r => r.Scope == scope && r.Type == type && r.Id == id)
				.DeleteAsync(ct);
		}
		finally { if (own) db.Dispose(); }
	}

	public async Task DeleteByTypeAsync(DataConnection? tx, string scope, string type, CancellationToken ct = default)
	{
		var (db, own) = Open(tx);
		try
		{
			await db.GetTable<Row>()
				.Where(r => r.Scope == scope && r.Type == type)
				.DeleteAsync(ct);
		}
		finally { if (own) db.Dispose(); }
	}

	public async Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default)
	{
		var qb = await _embedder.EmbedAsync([query], ct);
		var q = Truncate(qb.Vectors[0], _dim);
		var qmodel = qb.Model;
		var qdim = q.Length;

		using var db = _connect();
		// Model/dim guard: only cosine candidates embedded by the same model at the same dim as
		// the query, so we never compare incomparable vectors.
		var rowsQ = db.GetTable<Row>().Where(r => r.Scope == scope && r.Model == qmodel && r.Dim == qdim);
		if (filter.Type is not null) rowsQ = rowsQ.Where(r => r.Type == filter.Type);
		// Include-SET narrowing (`Type IN (...)`) — one brute-force pass over several containers
		// sharing this index (e.g. every memory store of a project), not one pass per container.
		if (filter.Types is { Count: > 0 } types) rowsQ = rowsQ.Where(r => types.Contains(r.Type));
		var rows = rowsQ.ToList();

		var candidates = rows.Select(r => (Key: r.Type + Sep + r.Id, Vec: VectorCodec.Decode(r.Vec)));
		var top = VectorMath.TopK(q, candidates, k);

		return top.Select(t =>
		{
			var sep = t.Key.IndexOf(Sep);
			return new Hit(t.Key[..sep], t.Key[(sep + 1)..], t.Score, "semantic");
		}).ToList();
	}

	// MRL (Matryoshka) truncation: the leading `dim` components of a Matryoshka embedding are a
	// usable lower-dim embedding on their own. Cosine renormalizes, so no rescale is needed.
	static float[] Truncate(float[] v, int dim) => dim > 0 && dim < v.Length ? v[..dim] : v;

	(DataConnection Db, bool Own) Open(DataConnection? tx) => tx is null ? (_connect(), true) : (tx, false);

	[Table("search_vec")]
	sealed class Row
	{
		[Column] public string Scope { get; set; } = string.Empty;
		[Column] public string Type { get; set; } = string.Empty;
		[Column] public string Id { get; set; } = string.Empty;
		[Column] public string Model { get; set; } = string.Empty;
		[Column] public int Dim { get; set; }
		[Column] public byte[] Vec { get; set; } = [];
	}
}
