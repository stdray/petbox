using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Search;
using PetBox.Core.Search.Eval;

namespace PetBox.Tests.Search;

// End-to-end pass of the eval harness — and, by being a real client of the read contract, the
// validation that the contract is the right seam for comparing strategies. The harness touches
// ONLY SearchService.SearchAsync: it never sees a DataConnection, an index type, or a score
// scale. Here it ranks two digest strategies (full-text vs title-only) on a deterministic,
// LLM-free corpus and proves it can tell them apart by recall@k — the empirical bench the
// design calls for (memory m-1a5c37fe).
public sealed class SearchEvalHarnessTests : IDisposable
{
	const string Scope = "proj/sessions";
	readonly string _dir;

	public SearchEvalHarnessTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-searcheval-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
	}

	public void Dispose()
	{
		TestDirs.CleanupOrDefer(_dir);
	}

	// A tiny labeled corpus: three "sessions" whose distinctive words live in the BODY but not
	// the TITLE, plus one query whose word is in both. Full-text indexing finds every query;
	// a title-only "digest" only finds the last → the metric must separate them.
	static readonly (string Id, string Title, string Body)[] Docs =
	[
		("s1", "Deploy notes", "deploy notes: разворачивание сервера through nginx reverse proxy"),
		("s2", "Memory design", "memory design: vector embeddings cosine similarity brute force"),
		("s3", "Tasks board", "tasks board: kanban columns workflow finite state machine"),
	];

	static readonly EvalQuery[] Queries =
	[
		Q("nginx", "s1"), // body-only
		Q("cosine", "s2"), // body-only
		Q("kanban", "s3"), // body-only
		Q("deploy", "s1"), // title + body
	];

	static EvalQuery Q(string text, string relevantId) =>
		new(Scope, text, new SearchFilter(), [new EvalJudgment("session", relevantId)]);

	// Build a SearchService over a fresh store, indexing each doc with the chosen text projection
	// (the "strategy" under test) inside one entity transaction.
	async Task<SearchService> BuildAsync(string name, Func<(string Id, string Title, string Body), string> text)
	{
		var cs = $"Data Source={Path.Combine(_dir, name + ".db")}";
		DataConnection Connect() => new(new DataOptions().UseSQLite(cs));

		using (var schema = Connect()) SqliteFtsIndex.EnsureSchema(schema);
		var svc = new SearchService([new SqliteFtsIndex(Connect)]);

		await using var db = Connect();
		using var tx = await db.BeginTransactionAsync();
		foreach (var d in Docs)
			await svc.IndexAsync(db, new SearchDoc(Scope, "session", d.Id, text(d)));
		await tx.CommitAsync();
		return svc;
	}

	[Fact]
	public async Task FullTextStrategy_ScoresPerfect_OnLexicalCorpus()
	{
		var full = await BuildAsync("full", d => d.Body);

		var report = await SearchEvalHarness.EvaluateAsync(full, Queries, k: 5);

		report.Queries.Should().Be(4);
		report.HitRateAtK.Should().Be(1.0);
		report.RecallAtK.Should().Be(1.0);
		report.MrrAtK.Should().Be(1.0); // each query's sole relevant hit lands at rank 1
		// Provenance: only the lexical Class-A index is wired → lexical ran on every query,
		// semantic never, nothing degraded.
		report.LexicalQueries.Should().Be(4);
		report.SemanticQueries.Should().Be(0);
		report.DegradedQueries.Should().Be(0);
	}

	[Fact]
	public async Task Harness_RanksFullTextAboveTitleOnlyDigest()
	{
		var full = await BuildAsync("full", d => d.Body);
		var digest = await BuildAsync("digest", d => d.Title);

		var cmp = await SearchEvalHarness.CompareAsync(full, digest, Queries, k: 5);

		// The whole point: the eval metric, read purely through the contract, separates the two
		// digest strategies.
		cmp.A.RecallAtK.Should().BeGreaterThan(cmp.B.RecallAtK);
		cmp.A.HitRateAtK.Should().Be(1.0);
		cmp.B.HitRateAtK.Should().Be(0.25); // title-only finds only "deploy"

		// They diverge exactly on the body-only queries; the title+body query agrees.
		cmp.DivergedQueries.Should().BeEquivalentTo(["nginx", "cosine", "kanban"]);
	}

	[Fact]
	public async Task EmptyCorpus_FindsNothing_AndScoresZero()
	{
		var full = await BuildAsync("full", d => d.Body);

		var report = await SearchEvalHarness.EvaluateAsync(
			full, [Q("nonexistentword", "s1")], k: 5);

		report.HitRateAtK.Should().Be(0.0);
		report.MrrAtK.Should().Be(0.0);
		report.PerQuery[0].Hit.Should().BeFalse();
		report.PerQuery[0].FirstRelevantRank.Should().Be(0);
	}
}
