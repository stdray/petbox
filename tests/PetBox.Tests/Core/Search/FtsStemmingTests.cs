using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Search;
using PetBox.Core.Settings;
using PetBox.Memory.Data;

namespace PetBox.Tests.SearchCore;

// The lexical floor's wordform recall (spec: search-lexical-multilingual): snowball
// stems widen the query (raw* OR stem*) and shadow the indexed text, with per-token
// script routing for mixed ru/en content. SQLite prefix-FTS alone cannot match
// «увеличить» ↔ «увеличили» — these tests pin that it now does.
public sealed class TokenStemmerTests
{
	[Fact]
	public void RussianWordforms_ShareAStem()
	{
		var a = TokenStemmer.Stem("увеличили");
		var b = TokenStemmer.Stem("увеличить");
		a.Should().Be(b);
		a.Should().NotBe("увеличили"); // it actually stemmed
	}

	[Fact]
	public void EnglishWordforms_ShareAStem()
	{
		TokenStemmer.Stem("vectorizations").Should().Be(TokenStemmer.Stem("vectorization"));
	}

	[Fact]
	public void MixedToken_RoutesByScript_DigitsPassThrough()
	{
		TokenStemmer.Stem("12345").Should().Be("12345");
		// A Cyrillic token is routed to the russian stemmer even in a latin-heavy text.
		TokenStemmer.Stem("буфера").Should().NotBe("буфера");
	}

	[Fact]
	public void ShadowTerms_EmitOnlyStemsThatDiffer()
	{
		var shadow = TokenStemmer.ShadowTerms("мы увеличили буфер до 8 КБ");
		shadow.Should().Contain(TokenStemmer.Stem("увеличили"));
		shadow.Should().NotContain("8"); // digits don't stem → no shadow
	}

	[Fact]
	public void BuildMatch_WidensStemmingTokens_AndKeepsPlainOnes()
	{
		var match = FtsQuery.BuildMatch("увеличить буфер 42");
		match.Should().Contain($"(увеличить* OR {TokenStemmer.Stem("увеличить")}*)");
		match.Should().Contain("42*");
	}

	[Fact]
	public void BuildMatch_EmptyQuery_IsNull()
	{
		FtsQuery.BuildMatch("   ").Should().BeNull();
		FtsQuery.BuildMatch("!!!").Should().BeNull();
	}
}

// The same promise end-to-end through the real FTS5 table.
[Collection("DataModule")]
public sealed class FtsStemmingIntegrationTests : IDisposable
{
	readonly string _dir;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly SqliteFtsIndex _fts;

	public FtsStemmingIntegrationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-ftsstem-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_factory.GetDb("proj", "notes"); // creates the file + search_fts
		_fts = new SqliteFtsIndex(() => _factory.NewConnection("proj", "notes"));
	}

	public void Dispose()
	{
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	Task Index(string id, string text) =>
		_fts.IndexAsync(_factory.GetDb("proj", "notes"), new SearchDoc("proj", "entry", id, text));

	async Task<IReadOnlyList<string>> Search(string query) =>
		(await _fts.SearchAsync("proj", query, new SearchFilter(null), 10)).Select(h => h.Id).ToList();

	[Fact]
	public async Task RussianWordform_QuerySideStem_FindsAnotherForm()
	{
		await Index("k1", "мы увеличили хвостовой буфер до 8 КБ");
		(await Search("увеличить буфер")).Should().Contain("k1");
	}

	[Fact]
	public async Task EnglishIrregularStem_NeedsTheDocumentShadow()
	{
		// happy → happi: the stem is NOT a prefix of the raw form, so the query's
		// stemmed leg (happi*) can only land on the document's SHADOW term.
		await Index("k1", "we were happy with the deploy");
		(await Search("happiness")).Should().Contain("k1");
	}

	[Fact]
	public async Task MixedQuery_RuAndIdentifiers_BothMustMatch()
	{
		await Index("k1", "починили вымышленный QuantumParser в билде ci.9999");
		await Index("k2", "починили совсем другое");
		var hits = await Search("починим quantumparser");
		hits.Should().Contain("k1");
		hits.Should().NotContain("k2"); // tokens are ANDed — identifier narrows
	}

	[Fact]
	public async Task ExactIdentifierSearch_DoesNotRegress()
	{
		await Index("k1", "fix in vectorization-ensure-schema-fix branch");
		(await Search("vectorization-ensure-schema-fix")).Should().Contain("k1");
	}
}
