using Kusto.Language;

namespace PetBox.Tests.Kql;

// Production-only coverage for the operators/functions the completion card wired: hasprefix / hassuffix
// (Kusto's TERM-prefix / TERM-suffix rule), has_any / has_all (their own syntax nodes), =~ / !~ and the
// isnull/isnotnull/isempty/isnotempty scalars. Every case runs over KqlBackendConfig.Active, so a dialect
// that translates one of the new shims differently goes red here.
//
// KqlStringOpsTests is deliberately NOT extended: a parallel branch (fix/kql-in-tilde-case-folding) owns
// that file and In(), so nothing here pins in~/!in~ either.
public sealed class KqlMissingStringOpsTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	// The doc-example seed: "North America" (the string the hasprefix/hassuffix pages use), an all-caps
	// value, and one with an underscore — which is a DELIMITER in Kusto's term model, not a term char.
	static readonly LogEntryRecord[] AffixRows =
	[
		new() { Id = 1, Message = "North America" },
		new() { Id = 2, Message = "LAKE MICHIGAN" },
		new() { Id = 3, Message = "rate_limit exceeded" },
	];

	// Row 2 carries no `env` property at all, so Properties["env"] is NULL for it — the null-operand probe.
	static readonly LogEntryRecord[] BagRows =
	[
		new() { Id = 1, Message = "dev row", PropertiesJson = """{"env":"dev"}""" },
		new() { Id = 2, Message = "no env property", PropertiesJson = """{"other":"x"}""" },
		new() { Id = 3, Message = "prod row", PropertiesJson = """{"env":"prod"}""" },
	];

	static readonly LogEntryRecord[] TermRows =
	[
		new() { Id = 1, Message = "starting up" },
		new() { Id = 2, Message = "crash on Earth" },
		new() { Id = 3, Message = "restart-required" },
	];

	// The one production run seam: seed `rows` into a fresh real store per backend and run the `where`.
	static void ShouldMatch(LogEntryRecord[] rows, string predicate, long[] expected)
	{
		foreach (var backend in KqlBackendConfig.Active)
			KqlTestHost.Apply(rows, Parse($"events | where {predicate}"), backend)
				.Select(r => r.Id).ToList()
				.Should().BeEquivalentTo(expected, $"'{predicate}' on {backend} must return {string.Join(",", expected)}");
	}

	static void ShouldThrow(LogEntryRecord[] rows, string predicate, string message)
	{
		var act = () => KqlTestHost.Apply(rows, Parse($"events | where {predicate}"), KqlBackend.Sqlite);
		act.Should().Throw<UnsupportedKqlException>().WithMessage(message);
	}

	// --- hasprefix / hassuffix: RHS is a prefix/suffix of ANY term in LHS ---
	//
	// Verified against kusto/query/hasprefix-operator, kusto/query/hassuffix-operator and the operators table
	// in datatypes-string-operators: `"North America" hasprefix "ame"` and `"North America" hassuffix "ica"`
	// are BOTH true, so the needle matches a term that is NOT the first/last one. The anchor is the term
	// boundary, and the free side is unconstrained — that is what makes `hasprefix`/`hassuffix` differ from
	// `startswith`/`endswith` (whole-string anchors) and from `has` (both term boundaries).
	[Theory]
	[InlineData("Message hasprefix 'ame'", new long[] { 1 })] // doc example: America, the SECOND term
	[InlineData("Message hasprefix 'nor'", new long[] { 1 })] // first term, case-insensitively
	[InlineData("Message hasprefix 'la'", new long[] { 2 })] // LAKE
	[InlineData("Message hasprefix 'mer'", new long[] { })] // inside a term, not at its start
	[InlineData("Message hasprefix 'rate'", new long[] { 3 })] // '_' is a delimiter, so "rate" is a term
	[InlineData("Message !hasprefix 'mer'", new long[] { 1, 2, 3 })]
	[InlineData("Message !hasprefix 'nor'", new long[] { 2, 3 })]
	[InlineData("Message hassuffix 'ica'", new long[] { 1 })] // doc example: America, the LAST term
	[InlineData("Message hassuffix 'th'", new long[] { 1 })] // North — not the last term, still a term suffix
	[InlineData("Message hassuffix 'americ'", new long[] { })] // ends mid-term
	[InlineData("Message hassuffix 'mich'", new long[] { })] // a term START is not a term suffix
	[InlineData("Message hassuffix 'gan'", new long[] { 2 })] // MICHIGAN
	[InlineData("Message !hassuffix 'ica'", new long[] { 2, 3 })]
	[InlineData("Message !hassuffix 'americ'", new long[] { 1, 2, 3 })]
	public void HasPrefix_HasSuffix_MatchTermBoundaries(string predicate, long[] expected) =>
		ShouldMatch(AffixRows, predicate, expected);

	// The `_cs` twins fold nothing. The pairs below are the ones that separate the two spellings: the same
	// needle at the same position, matched case-insensitively and NOT case-sensitively.
	[Theory]
	[InlineData("Message hasprefix 'AME'", new long[] { 1 })]
	[InlineData("Message hasprefix_cs 'Ame'", new long[] { 1 })]
	[InlineData("Message hasprefix_cs 'ame'", new long[] { })]
	[InlineData("Message !hasprefix_cs 'ame'", new long[] { 1, 2, 3 })]
	[InlineData("Message hassuffix 'ICA'", new long[] { 1 })]
	[InlineData("Message hassuffix_cs 'ica'", new long[] { 1 })]
	[InlineData("Message hassuffix_cs 'ICA'", new long[] { })]
	[InlineData("Message !hassuffix_cs 'ICA'", new long[] { 1, 2, 3 })]
	public void HasPrefix_HasSuffix_CsVariantsAreCaseSensitive(string predicate, long[] expected) =>
		ShouldMatch(AffixRows, predicate, expected);

	// An empty needle matches nothing (the `has` rule, HasFn/HasAffix share it), so its negation matches
	// every non-null value — the one arm where the positive is a compile-time constant.
	[Theory]
	[InlineData("Message hasprefix ''", new long[] { })]
	[InlineData("Message !hasprefix ''", new long[] { 1, 2, 3 })]
	[InlineData("Message hassuffix ''", new long[] { })]
	[InlineData("Message !hassuffix ''", new long[] { 1, 2, 3 })]
	public void HasPrefix_HasSuffix_EmptyNeedleMatchesNothing(string predicate, long[] expected) =>
		ShouldMatch(AffixRows, predicate, expected);

	// A TERM is a maximal run of letters/digits, so a needle holding ANY other character (`rate_l`'s
	// underscore, a space, a hyphen) cannot be a prefix/suffix of one — the answer is a constant false, not a
	// match. Left to the boundary regex this was a FALSE POSITIVE: `rate_limit hasprefix 'rate_l'` matched,
	// because the needle's underscore was treated as part of the anchored text instead of starting a new term.
	// The negation is the complement over non-null subjects (Negated keeps nulls out).
	[Theory]
	[InlineData("Message hasprefix 'rate'", new long[] { 3 })] // pure term chars: the real case, still works
	[InlineData("Message hasprefix 'rate_l'", new long[] { })] // + a delimiter → not a term prefix
	[InlineData("Message !hasprefix 'rate_l'", new long[] { 1, 2, 3 })] // …so its negation is everything
	[InlineData("Message hasprefix_cs 'rate_'", new long[] { })]
	[InlineData("Message hasprefix 'north am'", new long[] { })] // a space cannot begin a term's prefix
	[InlineData("Message hassuffix 'limit exceeded'", new long[] { })]
	[InlineData("Message hassuffix '_limit'", new long[] { })]
	[InlineData("Message !hassuffix '_limit'", new long[] { 1, 2, 3 })]
	[InlineData("Message hassuffix 'exceeded'", new long[] { 3 })] // pure term chars: the real case, still works
	[InlineData("Message hassuffix 'eded'", new long[] { 3 })] // mid-term is fine: a term SUFFIX
	public void HasPrefix_HasSuffix_NeedleContainingADelimiterMatchesNothing(string predicate, long[] expected) =>
		ShouldMatch(AffixRows, predicate, expected);

	// The null rule (the P1 from b59ec3a8): a NULL operand is excluded from the positive AND the negated
	// spelling. The whole has* family lowers to MatchesRegex, whose SQL PINS a null subject to FALSE, so a
	// bare NOT() would flip it to TRUE and KEEP the null row — every negated form must go through Negated().
	// Row 2 (no `env`) appears in NEITHER list.
	[Theory]
	[InlineData("Properties[\"env\"] hasprefix 'pro'", new long[] { 3 })]
	[InlineData("Properties[\"env\"] !hasprefix 'pro'", new long[] { 1 })]
	[InlineData("Properties[\"env\"] !hasprefix_cs 'pro'", new long[] { 1 })]
	// the delimiter-needle arm is a compile-time FALSE, so its negation must still exclude the null row
	[InlineData("Properties[\"env\"] !hasprefix 'pro_d'", new long[] { 1, 3 })]
	[InlineData("Properties[\"env\"] !hassuffix 'od_x'", new long[] { 1, 3 })]
	[InlineData("Properties[\"env\"] hassuffix 'rod'", new long[] { 3 })]
	[InlineData("Properties[\"env\"] !hassuffix 'rod'", new long[] { 1 })]
	[InlineData("Properties[\"env\"] !hassuffix_cs 'rod'", new long[] { 1 })]
	[InlineData("Properties[\"env\"] !~ 'prod'", new long[] { 1 })]
	public void NegatedForms_ExcludeNullOperands(string predicate, long[] expected) =>
		ShouldMatch(BagRows, predicate, expected);

	// --- has_any / has_all: OR / AND of plain `has` over the literal list ---

	[Theory]
	[InlineData("Message has_any ('crash', 'nothing-matches-this')", new long[] { 2 })]
	[InlineData("Message has_any ('EARTH', 'moon')", new long[] { 2 })] // case-insensitive, like has
	[InlineData("Message has_any ('art')", new long[] { })] // a term, not a substring — `contains` would match
	[InlineData("Message has_any ('crash', 'restart')", new long[] { 2, 3 })]
	[InlineData("Message has_all ('crash', 'earth')", new long[] { 2 })]
	[InlineData("Message has_all ('crash', 'moon')", new long[] { })]
	[InlineData("Message has_all ('restart', 'required')", new long[] { 3 })] // '-' is a delimiter
	[InlineData("Message has_all ('crash')", new long[] { 2 })]
	public void HasAny_HasAll_AreOrAndOfHas(string predicate, long[] expected) =>
		ShouldMatch(TermRows, predicate, expected);

	// The equivalence the operators are DEFINED by: has_any is the OR and has_all the AND of `has` over the
	// same terms. Asserted as row sets per backend, so a divergent lowering shows up as a set mismatch.
	[Fact]
	public void HasAny_HasAll_AgreeWithTheEquivalentHasCombination()
	{
		(string Any, string All, string Or, string And)[] cases =
		[
			("Message has_any ('crash', 'restart')", "Message has_all ('crash', 'restart')",
				"Message has 'crash' or Message has 'restart'", "Message has 'crash' and Message has 'restart'"),
			("Message has_any ('earth')", "Message has_all ('earth', 'crash')",
				"Message has 'earth'", "Message has 'earth' and Message has 'crash'"),
		];

		foreach (var backend in KqlBackendConfig.Active)
			foreach (var (any, all, or, and) in cases)
			{
				KqlTestHost.Apply(TermRows, Parse($"events | where {any}"), backend).Select(r => r.Id).ToList()
					.Should().BeEquivalentTo(KqlTestHost.Apply(TermRows, Parse($"events | where {or}"), backend).Select(r => r.Id),
						$"has_any on {backend} is the OR of the same `has` terms");
				KqlTestHost.Apply(TermRows, Parse($"events | where {all}"), backend).Select(r => r.Id).ToList()
					.Should().BeEquivalentTo(KqlTestHost.Apply(TermRows, Parse($"events | where {and}"), backend).Select(r => r.Id),
						$"has_all on {backend} is the AND of the same `has` terms");
			}
	}

	// An empty ELEMENT matches nothing: dropped from a has_any alternation, fatal to a has_all conjunction.
	[Theory]
	[InlineData("Message has_any ('', 'crash')", new long[] { 2 })]
	[InlineData("Message has_any ('')", new long[] { })]
	[InlineData("Message has_all ('', 'crash')", new long[] { })]
	public void HasAny_HasAll_EmptyElementMatchesNothing(string predicate, long[] expected) =>
		ShouldMatch(TermRows, predicate, expected);

	// A null subject matches nothing in either spelling (there is no negated has_any/has_all to restore).
	[Theory]
	[InlineData("Properties[\"env\"] has_any ('prod')", new long[] { 3 })]
	[InlineData("Properties[\"env\"] has_all ('prod')", new long[] { 3 })]
	[InlineData("Properties[\"env\"] has_any ('nothing')", new long[] { })]
	public void HasAny_HasAll_ExcludeNullSubjects(string predicate, long[] expected) =>
		ShouldMatch(BagRows, predicate, expected);

	[Theory]
	[InlineData("Message has_any (Level)", "'has_any' requires a list of string literals")]
	[InlineData("Message has_all (Level, 'x')", "'has_all' requires a list of string literals")]
	[InlineData("Level has_any ('1')", "'has_any' requires a string operand on the left, got Int32")]
	public void HasAny_HasAll_RejectNonLiteralListsOrNonStringLeft(string predicate, string message) =>
		ShouldThrow(TermRows, predicate, message);

	// --- =~ / !~: case-insensitive equality, NOT `==` ---

	static readonly LogEntryRecord[] CaseRows =
	[
		new() { Id = 1, Message = "crash on Earth" },
		new() { Id = 2, Message = "CRASH ON EARTH" },
		new() { Id = 3, Message = "unrelated" },
	];

	[Theory]
	[InlineData("Message =~ 'crash on Earth'", new long[] { 1, 2 })]
	[InlineData("Message =~ 'CRASH ON EARTH'", new long[] { 1, 2 })]
	[InlineData("Message !~ 'CRASH ON EARTH'", new long[] { 3 })]
	[InlineData("Message !~ 'nothing like it'", new long[] { 1, 2, 3 })]
	[InlineData("Message =~ 'nothing like it'", new long[] { })]
	// the contrast that makes `=~` worth having: `==` stays case-SENSITIVE
	[InlineData("Message == 'CRASH ON EARTH'", new long[] { 2 })]
	[InlineData("Message != 'CRASH ON EARTH'", new long[] { 1, 3 })]
	public void EqualsTilde_IsCaseInsensitiveEquality(string predicate, long[] expected) =>
		ShouldMatch(CaseRows, predicate, expected);

	// Null on EITHER side excludes the row from both spellings, including the negated one where the SQL
	// predicate would otherwise be NOT(NULL) → kept.
	[Theory]
	[InlineData("Properties[\"env\"] =~ 'PROD'", new long[] { 3 })]
	[InlineData("Properties[\"env\"] !~ 'PROD'", new long[] { 1 })]
	[InlineData("Properties[\"env\"] !~ 'DEV'", new long[] { 3 })]
	public void EqualsTilde_ExcludesNullOperands(string predicate, long[] expected) =>
		ShouldMatch(BagRows, predicate, expected);

	[Theory]
	[InlineData("Message =~ 3", "'=~' requires string operands, got string and long")]
	[InlineData("Level =~ 'x'", "'=~' requires string operands, got int and string")]
	public void EqualsTilde_RejectsNonStringOperands(string predicate, string message) =>
		ShouldThrow(CaseRows, predicate, message);

	// --- isnull / isnotnull / isempty / isnotempty ---

	// Row 2 has an EMPTY env value, row 4 has a null one (`{"env":null}` extracts as NULL), row 3 has none.
	static readonly LogEntryRecord[] NullRows =
	[
		new() { Id = 1, Message = "has env", PropertiesJson = """{"env":"dev","n":"12"}""" },
		new() { Id = 2, Message = "empty env", PropertiesJson = """{"env":"","n":"oops"}""" },
		new() { Id = 3, Message = "no env", PropertiesJson = """{"other":"x"}""" },
	];

	[Theory]
	[InlineData("isnull(Properties[\"env\"])", new long[] { 3 })]
	[InlineData("isnotnull(Properties[\"env\"])", new long[] { 1, 2 })]
	[InlineData("isempty(Properties[\"env\"])", new long[] { 2, 3 })] // null OR ''
	[InlineData("isnotempty(Properties[\"env\"])", new long[] { 1 })]
	[InlineData("isnull(toint(Properties[\"n\"]))", new long[] { 2, 3 })] // malformed and missing both null
	[InlineData("isnotnull(toint(Properties[\"n\"]))", new long[] { 1 })]
	[InlineData("isnull(Level)", new long[] { })] // a non-nullable value can never be null
	[InlineData("isnotnull(Level)", new long[] { 1, 2, 3 })]
	public void NullAndEmptyPredicates_Filter(string predicate, long[] expected) =>
		ShouldMatch(NullRows, predicate, expected);

	// isempty/isnotempty are string tests (Kusto does not define them for numbers): the error names the
	// function and the offending type instead of silently coercing.
	[Theory]
	[InlineData("isempty(Level)", "*isempty() requires a string argument*")]
	[InlineData("isnotempty(Level)", "*isnotempty() requires a string argument*")]
	[InlineData("isnull(Level, Level)", "*isnull() takes exactly 1 argument*")]
	[InlineData("isempty()", "*isempty() takes exactly 1 argument*")]
	public void NullAndEmptyPredicates_RejectBadArguments(string predicate, string message) =>
		ShouldThrow(NullRows, predicate, message);
}
