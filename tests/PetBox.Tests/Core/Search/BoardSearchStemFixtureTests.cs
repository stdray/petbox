using System.Text.Json;
using PetBox.Core.Search;

namespace PetBox.Tests.SearchCore;

// board-search-stem-lookup's acceptance gate: the server (TokenStemmer/FtsQuery, via
// BoardSearchIndexBuilder) and the client (ts/search-index.ts, a snowball-stemmers-backed port)
// stem independently — a divergence on even one rule means the client-built query silently
// misses a node the server-built lookup actually indexed. tests/fixtures/board-search-stem-
// fixture.json is the ONE file both sides assert against; src/PetBox.Web/ts/search-index.test.ts
// is the TS twin of this file. Keep the fixture (not this file) as the place new words/edge
// cases get added — this test just proves the C# side agrees with whatever's in it.
public sealed class BoardSearchStemFixtureTests
{
	sealed record StemCase(string Word, string Stem);
	sealed record TokenizeCase(string Text, IReadOnlyList<string> Tokens);
	sealed record Fixture(IReadOnlyList<StemCase> Stems, IReadOnlyList<TokenizeCase> Tokenize);

	static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

	static Fixture Load()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "tests", "fixtures", "board-search-stem-fixture.json");
			if (File.Exists(candidate))
				return JsonSerializer.Deserialize<Fixture>(File.ReadAllText(candidate), JsonOpts)
					?? throw new InvalidOperationException("fixture deserialized to null");
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException("tests/fixtures/board-search-stem-fixture.json not found walking up from the test bin.");
	}

	static readonly Fixture TheFixture = Load();

	public static IEnumerable<object[]> StemCases() => TheFixture.Stems.Select(c => new object[] { c.Word, c.Stem });
	public static IEnumerable<object[]> TokenizeCases() => TheFixture.Tokenize.Select(c => new object[] { c.Text, c.Tokens });

	[Theory]
	[MemberData(nameof(StemCases))]
	public void Stem_MatchesFixture(string word, string expectedStem) =>
		TokenStemmer.Stem(word).Should().Be(expectedStem, $"TokenStemmer.Stem({word}) must match the shared fixture (see search-index.test.ts for the TS side)");

	[Theory]
	[MemberData(nameof(TokenizeCases))]
	public void Tokenize_MatchesFixture(string text, IReadOnlyList<string> expectedTokens) =>
		FtsQuery.Tokens(text).Should().Equal(expectedTokens, $"FtsQuery.Tokens({text}) must match the shared fixture (see search-index.test.ts for the TS side)");

	// The fixture itself must not be trivially empty (a truncated/misnamed file would otherwise
	// pass both sides vacuously).
	[Fact]
	public void Fixture_IsNonEmpty()
	{
		TheFixture.Stems.Should().NotBeEmpty();
		TheFixture.Tokenize.Should().NotBeEmpty();
	}
}
