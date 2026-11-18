using Kusto.Language;

namespace PetBox.Tests.Kql;

// work/kql-has-family-term-needles: `has` / `has_cs` / `has_any` / `has_all` must REFUSE a needle that is not
// a whole TERM. Kusto's rule (datatypes-string-operators #What-is-a-term): RHS of `has` is a whole term in LHS,
// and a term is a MAXIMAL run of letters/digits — so a needle carrying a delimiter (space, `_`, `-`) cannot be
// one, whatever the value looks like. Before this card the boundary regex was built over the raw needle, so
// the answer depended on whether the needle's own delimiters happened to line up with the value's boundaries:
// "x a b y" has 'a b' was TRUE (Kusto: false). Every case runs over KqlBackendConfig.Active.
//
// Sibling coverage, deliberately NOT duplicated here: KqlMissingStringOpsTests pins the hasprefix/hassuffix
// half of the same rule plus the empty-list/empty-element identities.
public sealed class KqlHasTermNeedleTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	// Row 1 is the false-positive probe: `'a b'` is a SUBSTRING of it flanked by boundaries, and Kusto still
	// says false because 'a b' is two terms, not one. Row 3 is the Cyrillic term case (two terms, one of them
	// non-ASCII); row 4 shows `has 'rate_limit'` cannot match even the value that spells it out.
	static readonly LogEntryRecord[] Rows =
	[
		new() { Id = 1, Message = "x a b y" },
		new() { Id = 2, Message = "rate_limit exceeded" },
		new() { Id = 3, Message = "Привет мир" },
	];

	// Row 2 has no `env` property at all, so Properties["env"] is NULL — the null-operand probe for `!has`.
	static readonly LogEntryRecord[] BagRows =
	[
		new() { Id = 1, Message = "a b", PropertiesJson = """{"env":"dev row"}""" },
		new() { Id = 2, Message = "no env property", PropertiesJson = """{"other":"x"}""" },
		new() { Id = 3, Message = "prod row", PropertiesJson = """{"env":"prod row"}""" },
	];

	static void ShouldMatch(LogEntryRecord[] rows, string predicate, long[] expected)
	{
		foreach (var backend in KqlBackendConfig.Active)
			KqlTestHost.Apply(rows, Parse($"events | where {predicate}"), backend)
				.Select(r => r.Id).ToList()
				.Should().BeEquivalentTo(expected, $"'{predicate}' on {backend} must return {string.Join(",", expected)}");
	}

	// The delimiter needle is a COMPILE-TIME constant per operator, so the proof that nothing was lowered to a
	// regex is that the same needle with its delimiter removed still matches — a broken needle never does.
	// `rate_limit` is two terms (`rate`, `limit`), so the needle that spells the VALUE out is false as well:
	// that is the rule, not a typo.

	[Theory]
	[InlineData("Message has 'a'", new long[] { 1 })] // control: a real single term
	[InlineData("Message has 'a b'", new long[] { })] // space → two terms
	[InlineData("Message has 'a b y'", new long[] { })]
	[InlineData("Message has 'rate'", new long[] { 2 })] // control: 'rate' IS a term of "rate_limit"
	[InlineData("Message has 'rate_l'", new long[] { })] // '_' is a delimiter (not a word char)
	[InlineData("Message has 'rate_limit'", new long[] { })] // two terms cannot be one whole term
	[InlineData("Message has 'limit exceeded'", new long[] { })]
	public void Has_NeedleContainingADelimiterMatchesNothing(string predicate, long[] expected) =>
		ShouldMatch(Rows, predicate, expected);

	[Theory]
	[InlineData("Message has_cs 'a'", new long[] { 1 })]
	[InlineData("Message has_cs 'a b'", new long[] { })]
	[InlineData("Message has_cs 'rate_l'", new long[] { })]
	[InlineData("Message has_cs 'rate'", new long[] { 2 })]
	public void HasCs_NeedleContainingADelimiterMatchesNothing(string predicate, long[] expected) =>
		ShouldMatch(Rows, predicate, expected);

	// The `!` spelling is the complement over NON-NULL subjects: the constant-false needle still goes through
	// Negated(), so a null operand is excluded from it too (row 2 of BagRows appears in neither list).
	[Theory]
	[InlineData("Properties[\"env\"] has 'dev'", new long[] { 1 })]
	[InlineData("Properties[\"env\"] !has 'dev'", new long[] { 3 })]
	[InlineData("Properties[\"env\"] !has 'de v'", new long[] { 1, 3 })] // delimiter → nothing matches, negation keeps non-nulls
	[InlineData("Properties[\"env\"] !has_cs 'de v'", new long[] { 1, 3 })]
	public void NegatedHas_DelimiterNeedleStillExcludesNulls(string predicate, long[] expected) =>
		ShouldMatch(BagRows, predicate, expected);

	// A UNICODE term: `has` is case-insensitive over the whole Unicode range (the (?i) regex lowering), and a
	// Cyrillic needle must behave exactly like an ASCII one — the term it is, or a constant false when the
	// needle spans a space.
	[Theory]
	[InlineData("Message has 'привет'", new long[] { 3 })] // folds case: "Привет"
	[InlineData("Message has 'ПРИВЕТ'", new long[] { 3 })]
	[InlineData("Message has 'мир'", new long[] { 3 })] // the second term
	[InlineData("Message has 'ривет'", new long[] { })] // a term is not a substring: no left boundary
	[InlineData("Message has 'привет мир'", new long[] { })] // space → not a term
	[InlineData("Message has_cs 'Привет'", new long[] { 3 })]
	[InlineData("Message has_cs 'привет'", new long[] { })] // _cs folds nothing, Cyrillic included
	public void Has_CyrillicTermAndDelimiter(string predicate, long[] expected) =>
		ShouldMatch(Rows, predicate, expected);

	// has_any / has_all, the PINNED per-element rule for an element that can never match:
	//   * has_any — it can never match, so it is DROPPED; the remaining elements still can (`'мир'` still
	//     finds row 3 through the same list that carries an impossible `'a b'`).
	//   * has_all — every element must match, so ONE impossible element makes the whole predicate false, and
	//     the row is excluded even though the other element matches it (row 2 below).
	[Theory]
	[InlineData("Message has_any ('a b', 'мир')", new long[] { 3 })] // impossible element dropped
	[InlineData("Message has_any ('a b', 'a b y')", new long[] { })] // all impossible → the false identity
	[InlineData("Message has_any ('a b')", new long[] { })]
	[InlineData("Message has_any ('rate_l', 'rate')", new long[] { 2 })] // the term element still matches
	[InlineData("Message has_all ('rate', 'limit')", new long[] { 2 })] // both terms present → true
	[InlineData("Message has_all ('rate', 'a b')", new long[] { })] // one impossible element → whole false
	[InlineData("Message has_all ('rate_limit', 'rate')", new long[] { })]
	[InlineData("Message has_all ('a b')", new long[] { })]
	public void HasAnyHasAll_PinThePerElementRuleForANonTermElement(string predicate, long[] expected) =>
		ShouldMatch(Rows, predicate, expected);
}
