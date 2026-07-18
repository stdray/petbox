using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Search;

namespace PetBox.Tests.Search;

// search-doc-model-title-weights: the лексическая нога weights fields Key > Title > Tags > Body, so
// a query term that lands in a doc's TITLE outranks the same term buried in another doc's BODY. This
// exercises the REAL SqliteFtsIndex against the REAL fts5 build the app ships — the one place the
// bm25() weight vector's positional mapping over the (partly UNINDEXED) columns is actually proved.
// Before this slice bm25 ran with no column weights, and both docs would rank by raw term frequency
// alone; the title doc could easily lose to a body doc that merely repeats the term.
public sealed class FtsColumnWeightTests : IDisposable
{
	const string Scope = "proj/notes";
	readonly string _dir;
	readonly string _cs;

	public FtsColumnWeightTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-fts-weights-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_cs = $"Data Source={Path.Combine(_dir, "store.db")}";
		SearchTestSchema.Ensure(_cs);
	}

	public void Dispose() => TestDirs.CleanupOrDefer(_dir);

	DataConnection Connect() => new(new DataOptions().UseSQLite(_cs));

	async Task IndexAsync(SqliteFtsIndex fts, params SearchDoc[] docs)
	{
		await using var db = Connect();
		using var tx = await db.BeginTransactionAsync();
		foreach (var d in docs)
			await fts.IndexAsync(db, d);
		await tx.CommitAsync();
	}

	[Fact]
	public async Task TitleHit_OutranksBodyHit_ForTheSameTerm()
	{
		var fts = new SqliteFtsIndex(Connect);
		// A CLEAN discriminator: each doc contains the query term exactly ONCE, and the two docs are
		// otherwise identical in shape (same single token, same length) — the ONLY difference is which
		// COLUMN the term sits in. With equal column weights (the pre-slice behaviour) these tie; with
		// Title (2) > Body (1) the titled doc must come first. Nothing but the weighting can order them.
		await IndexAsync(fts,
			new SearchDoc(Scope, "note", "titled", Text: "", Title: "marmot"),
			new SearchDoc(Scope, "note", "bodied", Text: "marmot", Title: ""));

		var hits = await fts.SearchAsync(Scope, "marmot", new SearchFilter(), k: 10);

		hits.Select(h => h.Id).Should().Equal("titled", "bodied");
	}

	[Fact]
	public void Weights_FormTheDescendingImportanceLadder()
	{
		// Key > Title = Tags > Body is the whole point (search-doc-model-title-weights). The POSITIONAL
		// mapping onto the (partly UNINDEXED) columns is proved behaviourally by
		// TitleHit_OutranksBodyHit_ForTheSameTerm above and the column ORDER is pinned by the golden
		// schema snapshot; this just guards the ladder's shape so a future edit can't quietly flatten
		// or invert it.
		FtsColumnWeights.Key.Should().BeGreaterThan(FtsColumnWeights.Title);
		FtsColumnWeights.Title.Should().Be(FtsColumnWeights.Tags);
		FtsColumnWeights.Title.Should().BeGreaterThan(FtsColumnWeights.Body);
		FtsColumnWeights.Body.Should().Be(FtsColumnWeights.Unindexed); // body is the baseline weight
	}
}
