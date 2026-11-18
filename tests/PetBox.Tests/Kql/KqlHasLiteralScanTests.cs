using Kusto.Language;

namespace PetBox.Tests.Kql;

// `has` / `has_cs` / `has_any` / `has_all` are an INDEX PRE-FILTER plus an EXACT LITERAL SCAN, not
// term-membership — so a needle CARRYING a delimiter is a legitimate match and must stay one. Evidence:
// real-ADX behaviour in Stack Overflow 79092741 (`Message has "Heart Beat"` matches, "as if it was a single
// whole term") and SO 74827840 (`has "#ell#"` — "index for initial filtering on the term (ell), followed by a
// narrowed data scan (to filter out on the entire searched string)"); the docs sentence "RHS is a whole term in
// LHS" is a SIMPLIFICATION of that implementation. A strict reading was tried on this branch and REVERTED: it
// turned the most natural user query into a silent empty result — the exact failure class this wave exists to
// remove. See the block comment above KqlScalar.HasFn; do not "fix" it back to strictness.
//
// Every case runs over KqlBackendConfig.Active. Row ids and counts below are MEASURED on the restored engine
// (ffdda0b3 semantics), not derived from the regex.
//
// Known narrowness, deliberately NOT pinned: the scan anchors BOTH ends of the needle, so `has 'rate_l'` finds
// "rate_l exceeded" (row 5) but NOT "rate_limit exceeded" (row 2), whose next character is a letter. Whether
// real ADX matches that middle case is unverified here; it is left unasserted rather than locked in either way.
public sealed class KqlHasLiteralScanTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	static readonly LogEntryRecord[] Rows =
	[
		new() { Id = 1, Message = "x a b y" },              // needle with SPACES, delimited inside the value
		new() { Id = 2, Message = "rate_limit exceeded" },  // the needle that spells this out is row 2's own words
		new() { Id = 3, Message = "Привет мир" },            // Cyrillic, two terms
		new() { Id = 4, Message = "Heart Beat now" },        // the SO 79092741 shape: a space-bearing needle
		new() { Id = 5, Message = "rate_l exceeded" },       // a delimiter-bearing needle, fully delimited
	];

	// Row 2 has no `env` property, so Properties["env"] is NULL — the null-operand probe for the negated forms.
	static readonly LogEntryRecord[] BagRows =
	[
		new() { Id = 1, Message = "a b", PropertiesJson = """{"env":"de v row"}""" },
		new() { Id = 2, Message = "no env property", PropertiesJson = """{"other":"x"}""" },
		new() { Id = 3, Message = "prod row", PropertiesJson = """{"env":"prod row"}""" },
	];

	static List<long> Ids(LogEntryRecord[] rows, string predicate, KqlBackend backend) =>
		KqlTestHost.Apply(rows, Parse($"events | where {predicate}"), backend).Select(r => r.Id).ToList();

	static void ShouldMatch(LogEntryRecord[] rows, string predicate, long[] expected)
	{
		foreach (var backend in KqlBackendConfig.Active)
			Ids(rows, predicate, backend).Should().BeEquivalentTo(expected,
				$"'{predicate}' on {backend} must return {string.Join(",", expected)}");
	}

	// The PERMISSIVE core: a needle with spaces, underscores or a hyphen matches when it is delimited in the
	// value — including the whole value spelled out — and the pure-term needles keep matching as before.
	[Theory]
	[InlineData("Message has 'a b'", new long[] { 1 })]              // "x a b y": delimited on both sides
	[InlineData("Message has 'a b y'", new long[] { 1 })]            // tail of the value
	[InlineData("Message has 'Heart Beat'", new long[] { 4 })]       // SO 79092741's exact shape
	[InlineData("Message has 'heart beat'", new long[] { 4 })]       // …and `has` folds case over a spaced needle
	[InlineData("Message has 'rate_l'", new long[] { 5 })]           // "rate_l exceeded": delimiter inside the needle
	[InlineData("Message has 'rate_limit'", new long[] { 2 })]       // the needle that spells the value out
	[InlineData("Message has 'limit exceeded'", new long[] { 2 })]
	[InlineData("Message has 'rate'", new long[] { 2, 5 })]          // control: a pure term still works
	[InlineData("Message has 'a'", new long[] { 1 })]
	public void Has_DelimiterNeedleMatchesWhenDelimited(string predicate, long[] expected) =>
		ShouldMatch(Rows, predicate, expected);

	// `has_cs` folds nothing — the same spaced needle, matched and NOT matched.
	[Theory]
	[InlineData("Message has_cs 'Heart Beat'", new long[] { 4 })]
	[InlineData("Message has_cs 'heart beat'", new long[] { })]
	[InlineData("Message has_cs 'a b'", new long[] { 1 })]
	[InlineData("Message has_cs 'rate_l'", new long[] { 5 })]
	public void HasCs_DelimiterNeedleIsCaseSensitiveOnly(string predicate, long[] expected) =>
		ShouldMatch(Rows, predicate, expected);

	// The negated spellings are the complement over NON-NULL subjects: a spaced needle that matches nothing
	// still goes through Negated(), so a null operand stays excluded (BagRows row 2 appears in neither list).
	[Theory]
	[InlineData("Properties[\"env\"] has 'de v'", new long[] { 1 })]     // spaced needle matches "de v row"
	[InlineData("Properties[\"env\"] has 'prod row'", new long[] { 3 })]
	[InlineData("Properties[\"env\"] !has 'de v'", new long[] { 3 })]
	[InlineData("Properties[\"env\"] !has 'zz zz'", new long[] { 1, 3 })]
	[InlineData("Properties[\"env\"] !has_cs 'de v'", new long[] { 3 })]
	public void NegatedHas_DelimiterNeedleStillExcludesNulls(string predicate, long[] expected) =>
		ShouldMatch(BagRows, predicate, expected);

	// A UNICODE needle behaves exactly like an ASCII one: the (?i) lowering folds Cyrillic on both backends, and
	// a spaced Cyrillic needle matches its delimited occurrence.
	[Theory]
	[InlineData("Message has 'привет'", new long[] { 3 })]
	[InlineData("Message has 'ПРИВЕТ'", new long[] { 3 })]
	[InlineData("Message has 'мир'", new long[] { 3 })]
	[InlineData("Message has 'привет мир'", new long[] { 3 })]  // spaced needle, whole value
	[InlineData("Message has 'ривет'", new long[] { })]          // a term is not matched mid-word
	[InlineData("Message has_cs 'Привет'", new long[] { 3 })]
	[InlineData("Message has_cs 'привет'", new long[] { })]      // _cs folds nothing, Cyrillic included
	public void Has_CyrillicTermAndSpacedNeedle(string predicate, long[] expected) =>
		ShouldMatch(Rows, predicate, expected);

	// has_any / has_all are DEFINED as the OR / AND of plain `has` over the same elements, and a delimiter-bearing
	// element gets no special per-element rule of its own any more: it is just an element like any other. Pinned
	// per element, so a delimiter element that CAN match (`'a b'`) and one that cannot (`'zz zz'`) are both shown.
	[Theory]
	[InlineData("Message has_any ('a b', 'мир')", new long[] { 1, 3 })]
	[InlineData("Message has_any ('a b', 'zz zz')", new long[] { 1 })] // the impossible element adds nothing
	[InlineData("Message has_any ('zz zz')", new long[] { })]
	[InlineData("Message has_any ('rate', 'Heart Beat')", new long[] { 2, 4, 5 })]
	[InlineData("Message has_all ('Heart Beat')", new long[] { 4 })]
	[InlineData("Message has_all ('rate', 'limit exceeded')", new long[] { 2 })]
	[InlineData("Message has_all ('a b', 'rate')", new long[] { })]    // row 1 has 'a b' but no 'rate'
	[InlineData("Message has_all ('zz zz', 'rate')", new long[] { })]  // and neither does any row
	public void HasAnyHasAll_AreTheOrAndOfPlainHas(string predicate, long[] expected) =>
		ShouldMatch(Rows, predicate, expected);

	// The definition itself, asserted rather than assumed: for every list below, has_any/has_all must return
	// exactly what the OR/AND of the individual `has` predicates returns — delimiter elements included.
	[Theory]
	[InlineData("'a b'", "'мир'")]
	[InlineData("'Heart Beat'", "'rate'")]
	[InlineData("'zz zz'", "'rate'")]
	[InlineData("'rate_l'", "'rate_limit'")]
	public void HasAnyHasAll_MatchTheirOrAndExpansion(string first, string second)
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var anyOf = Ids(Rows, $"Message has_any ({first}, {second})", backend);
			var orOf = Ids(Rows, $"Message has {first} or Message has {second}", backend);
			anyOf.Should().BeEquivalentTo(orOf, $"has_any is the OR of the same `has` terms on {backend}");

			var allOf = Ids(Rows, $"Message has_all ({first}, {second})", backend);
			var andOf = Ids(Rows, $"Message has {first} and Message has {second}", backend);
			allOf.Should().BeEquivalentTo(andOf, $"has_all is the AND of the same `has` terms on {backend}");
		}
	}
}
