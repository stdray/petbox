using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Search;

namespace PetBox.Tests.Search;

// search-legs-tie-break-nondeterministic: the lexical leg orders solely by bm25
// (SqliteFtsIndex.cs) with no tie-break, so rows with an EQUAL score fall back to whatever
// order SQLite's fts5 MATCH happens to return them in — which tracks rowid (insertion) order,
// not any property of the query. Two independent pool rebuilds that insert the same
// equal-scoring documents in a DIFFERENT order (a realistic case: nothing here promises a
// stable insertion order across restarts) must still search-sort them identically. Regressing
// the ThenBy(Type).ThenBy(Id) fix in SqliteFtsIndex.cs reproduces exactly the two divergent
// orders asserted against below (rowid order tracks insertion order, so rebuild A and rebuild B
// disagree) — this is not a coin flip, it is SQLite's well-established (if unofficial) rowid
// scan order for a simple equal-rank match, which is what makes the failure reliable rather
// than flaky.
public sealed class SqliteFtsTieBreakTests : IDisposable
{
	const string Scope = "proj/notes";
	readonly List<string> _dirs = [];

	public void Dispose()
	{
		foreach (var dir in _dirs) TestDirs.CleanupOrDefer(dir);
	}

	SqliteFtsIndex NewIndex(out Func<DataConnection> connect)
	{
		var dir = Path.Combine(Path.GetTempPath(), "petbox-fts-tiebreak-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		_dirs.Add(dir);
		var cs = $"Data Source={Path.Combine(dir, "store.db")}";
		SearchTestSchema.Ensure(cs);
		connect = () => new DataConnection(new DataOptions().UseSQLite(cs));
		return new SqliteFtsIndex(connect);
	}

	static async Task IndexAsync(SqliteFtsIndex fts, Func<DataConnection> connect, params SearchDoc[] docs)
	{
		await using var db = connect();
		using var tx = await db.BeginTransactionAsync();
		foreach (var d in docs)
			await fts.IndexAsync(db, d);
		await tx.CommitAsync();
	}

	// Three docs whose Title/Tags/Body shape is IDENTICAL (same single Body token, everything
	// else empty) so bm25 scores them EQUAL — the only thing left to order them is the tie-break.
	static SearchDoc Doc(string id) => new(Scope, "note", id, Text: "marmot");

	[Fact]
	public async Task EqualScoreRows_SortByDocumentAddress_RegardlessOfInsertionOrder()
	{
		// Rebuild A: insert in one order.
		var ftsA = NewIndex(out var connectA);
		await IndexAsync(ftsA, connectA, Doc("zzz"), Doc("mmm"), Doc("aaa"));
		var hitsA = await ftsA.SearchAsync(Scope, "marmot", new SearchFilter(), k: 10);

		// Rebuild B: same equal-scoring documents, inserted in the OPPOSITE order — standing in
		// for a second, independent pool rebuild (process restart / cold cache) that happens to
		// process the same corpus in a different sequence.
		var ftsB = NewIndex(out var connectB);
		await IndexAsync(ftsB, connectB, Doc("aaa"), Doc("mmm"), Doc("zzz"));
		var hitsB = await ftsB.SearchAsync(Scope, "marmot", new SearchFilter(), k: 10);

		var expected = new[] { "aaa", "mmm", "zzz" }; // ordinal address order
		hitsA.Select(h => h.Id).Should().Equal(expected);
		hitsB.Select(h => h.Id).Should().Equal(expected);
	}
}
