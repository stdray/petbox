using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.Mapping;
using PetBox.Core.Data;
using PetBox.Core.Search;
using PetBox.Core.Search.Eval;
using PetBox.Core.Settings;
using PetBox.Memory.Data;
using Xunit.Abstractions;

namespace PetBox.Tests.SearchCore;

// The promised BEFORE/AFTER number for the stemming change (work: fts-snowball-stemming):
// SearchEvalHarness.CompareAsync over a labeled ru/en WORDFORM corpus — every query uses
// a different morphological form than its document (declensions, conjugations, plurals),
// plus identifier controls. Strategy A = the pre-stemming floor (raw text, `tok*` match);
// strategy B = the shipped one (shadow stems + `(tok* OR stem*)`). The corpus includes
// forms stemming is NOT expected to fix (suppletive/aspect pairs) to keep the number honest.
public sealed class FtsStemmingEvalTests : IDisposable
{
	readonly ITestOutputHelper _output;
	readonly string _dir;
	readonly ScopedDbFactory<MemoryDb> _factory;

	public FtsStemmingEvalTests(ITestOutputHelper output)
	{
		_output = output;
		_dir = Path.Combine(Path.GetTempPath(), "petbox-stemeval-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
	}

	public void Dispose()
	{
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	// (doc id, text) — petbox-flavored mixed ru/en content.
	static readonly (string Id, string Text)[] Docs =
	[
		("buffer",   "мы увеличили хвостовой буфер парсера до 8 КБ"),
		("deploy",   "запустили деплой на проде после зелёного CI"),
		("vec",      "починили векторизацию: воркер падал каждый тик"),
		("sessions", "архив сессий хранится как latest-snapshot со сжатием"),
		("digest",   "дистилляция дайджестов работает фоновым воркером"),
		("search",   "гибридный поиск сливает лексику и семантику через RRF"),
		("errors",   "в логах накопились ошибки таймаутов после рестарта"),
		("delete",   "удалили мусорные записи из стора вчера вечером"),
		("trace",    "трейсы показывают водопад спанов по запросу"),
		("docs",     "документация описывает методологию проекта"),
		("happy",    "we were happy with the deployment results"),
		("fixes",    "the latest fixes landed in the main branch"),
		("studies",  "several studies confirm the retrieval gains"),
		("ident",    "fix lives in vectorization-ensure-schema-fix branch"),
		("running",  "the worker keeps running after the restart"),
		("config",   "конфигурация резолвится по тегам окружения"),
	];

	// Queries use a DIFFERENT wordform than the document. `expectStemFix` marks the cases
	// the stemmer is supposed to win; false = control (prefix already works, or known-hard
	// suppletive/aspect/derivation pairs stemming cannot unify).
	static readonly (string Query, string Doc, bool ExpectStemFix)[] Queries =
	[
		("увеличить буфер", "buffer", true),
		("увеличила буферы", "buffer", true),
		("запустить деплой", "deploy", true),
		("запустим деплой", "deploy", true),
		("починить векторизацию", "vec", true),
		("починим воркер векторизации", "vec", true),
		("архивы сессии", "sessions", true),
		("сессия в архиве", "sessions", true),
		("дистиллировать дайджест", "digest", false), // деривация глагол↔сущ. — вне обещаний стеммера
		("дайджесты дистилляции", "digest", true),
		("гибридного поиска", "search", true),
		("слить лексику", "search", false),           // слить/сливает — видовая пара, стемы расходятся
		("ошибка таймаута", "errors", true),
		("удалить мусорную запись", "delete", true),
		("трейс показал водопад", "trace", false),    // показал/показывают — видовая пара, стемы расходятся
		("методологии проектов", "docs", true),
		("happiness with deployments", "happy", true),
		("happier deployment", "happy", false),     // comparative → 'happier' stem ≠ 'happi'
		("fixing the branch", "fixes", true),
		("studying retrieval", "studies", true),
		("vectorization-ensure-schema-fix", "ident", false), // identifier control — must not regress
		("runs after restart", "running", false),   // run* vs running: prefix already matches
		("конфигурацию по тегам", "config", true),
		("резолвить конфигурацию", "config", true),
		("упал воркер", "vec", false),              // упал/падал — suppletive-ish, stem won't unify
		("буферизация парсера", "buffer", false),   // derivation (буферизация ≠ буфер stem)
	];

	[Fact]
	public async Task Stemming_BeatsThePreStemmingFloor_OnWordformCorpus()
	{
		// Two identical corpora in two store files: A indexed raw (legacy), B through the
		// shipped SqliteFtsIndex (shadow stems).
		using var dbBase = _factory.GetDb("proj", "base");
		using var dbStem = _factory.GetDb("proj", "stem");
		var legacy = new LegacyFtsIndex(() => _factory.NewEnsuredConnection("proj", "base"));
		var shipped = new SqliteFtsIndex(() => _factory.NewEnsuredConnection("proj", "stem"));
		foreach (var (id, text) in Docs)
		{
			await legacy.IndexAsync(dbBase, new SearchDoc("proj", "entry", id, text));
			await shipped.IndexAsync(dbStem, new SearchDoc("proj", "entry", id, text));
		}

		var corpus = Queries
			.Select(q => new EvalQuery("proj", q.Query, new SearchFilter(null), [new EvalJudgment("entry", q.Doc)]))
			.ToList();

		var cmp = await SearchEvalHarness.CompareAsync(
			new SearchService([legacy]), new SearchService([shipped]), corpus, k: 5);

		_output.WriteLine($"queries: {cmp.A.Queries} (expect-fix: {Queries.Count(q => q.ExpectStemFix)}, controls: {Queries.Count(q => !q.ExpectStemFix)})");
		_output.WriteLine($"baseline (raw + tok*):        hit@5={cmp.A.HitRateAtK:F3} recall@5={cmp.A.RecallAtK:F3} mrr@5={cmp.A.MrrAtK:F3}");
		_output.WriteLine($"stemmed  (shadow + raw|stem): hit@5={cmp.B.HitRateAtK:F3} recall@5={cmp.B.RecallAtK:F3} mrr@5={cmp.B.MrrAtK:F3}");
		_output.WriteLine($"diverged queries: {cmp.DivergedQueries.Count}");
		foreach (var (q, i) in Queries.Select((q, i) => (q, i)))
		{
			var a = cmp.A.PerQuery[i].Hit ? "hit " : "MISS";
			var b = cmp.B.PerQuery[i].Hit ? "hit " : "MISS";
			if (a != b) _output.WriteLine($"  [{a}→{b}] {q.Query}");
		}

		// The stemmed floor must strictly beat the baseline on this corpus and must hit
		// every query the stemmer is expected to fix; controls must not regress.
		cmp.B.HitRateAtK.Should().BeGreaterThan(cmp.A.HitRateAtK);
		for (var i = 0; i < Queries.Length; i++)
		{
			if (Queries[i].ExpectStemFix)
				cmp.B.PerQuery[i].Hit.Should().BeTrue($"stemming should fix: {Queries[i].Query}");
			if (cmp.A.PerQuery[i].Hit)
				cmp.B.PerQuery[i].Hit.Should().BeTrue($"must not regress: {Queries[i].Query}");
		}
	}

	// The PRE-stemming Class-A floor, frozen for the A side of the comparison: raw text,
	// every query token prefix-matched and implicitly ANDed (the old FtsQuery.BuildMatch).
	sealed class LegacyFtsIndex(Func<DataConnection> connect) : ISearchIndex
	{
		public SearchConsistency ConsistencyClass => SearchConsistency.Synchronous;
		public SearchCapability Capability => SearchCapability.Lexical;

		public async Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default)
		{
			var db = tx!;
			await db.GetTable<Row>()
				.Where(r => r.Scope == doc.Scope && r.Type == doc.Type && r.Id == doc.Id)
				.DeleteAsync(ct);
			await db.InsertAsync(new Row { Scope = doc.Scope, Type = doc.Type, Id = doc.Id, Text = doc.Text, Tags = doc.Tags ?? "" }, token: ct);
		}

		public Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default) =>
			throw new NotSupportedException();

		public Task DeleteByTypeAsync(DataConnection? tx, string scope, string type, CancellationToken ct = default) =>
			throw new NotSupportedException();

		public async Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default)
		{
			var match = string.Join(' ', FtsQuery.Tokens(query).Select(t => t + "*"));
			if (match.Length == 0) return [];
			using var db = connect();
			var rows = db.GetTable<Row>()
				.Where(r => r.Scope == scope && Sql.Ext.SQLite().Match(r, match))
				.OrderBy(r => Sql.Ext.SQLite().Rank(r))
				.Take(k)
				.Select(r => new { r.Type, r.Id, Rank = Sql.Ext.SQLite().Rank(r) })
				.ToList();
			await Task.CompletedTask;
			return rows.Select(r => new Hit(r.Type, r.Id, -(r.Rank ?? 0d), "lexical")).ToList();
		}

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
}
