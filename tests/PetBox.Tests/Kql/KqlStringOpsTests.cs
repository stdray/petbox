using Kusto.Language;

namespace PetBox.Tests.Kql;

// Production-only coverage for the KQL string operators and functions. The term-vs-substring
// distinction of `has`, and parse_json's string passthrough, cannot go through the differential
// suite: KustoLoco models `has` as a substring and returns a dynamic for parse_json, so those
// specifics are pinned here against the production engine directly.
public sealed class KqlStringOpsTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	static readonly LogEntryRecord[] Rows =
	[
		new() { Id = 1, Level = (int)LogLevel.Information, Message = "starting up", ServiceKey = "svc-a" },
		new() { Id = 2, Level = (int)LogLevel.Error, Message = "crash on Earth", ServiceKey = "svc-b" },
		new() { Id = 3, Level = (int)LogLevel.Warning, Message = "restart-required", ServiceKey = "svc-a" },
		new() { Id = 4, Level = (int)LogLevel.Information, Message = "BOOM normalized", ServiceKey = "svc-c" },
	];

	// The one production run seam: seed Rows into a fresh in-memory LogDb and Apply/Execute `kql` over the
	// real linq2db IQueryable. Sqlite is the only Active backend today.
	static IReadOnlyList<long> Ids(string kql) =>
		KqlTestHost.Apply(Rows, Parse(kql), KqlBackend.Sqlite).Select(r => r.Id).ToList();

	static async Task<List<object?[]>> Table(string kql)
	{
		var (_, rows) = await KqlTestHost.ExecuteAsync(Rows, Parse(kql), KqlBackend.Sqlite);
		return rows;
	}

	// The heart of honest `has`: a substring that is not a whole term does NOT match, whereas
	// `contains` would. "art" lives inside "starting"/"restart"/"Earth" but is never a term.
	[Fact]
	public void Has_IsTermMatch_NotSubstring()
	{
		Ids("events | where Message has 'art'").Should().BeEmpty();
		Ids("events | where Message contains 'art'").Should().BeEquivalentTo([1L, 2L, 3L]);
	}

	[Fact]
	public void Has_MatchesWholeTermAcrossNonAlphanumericBoundaries()
	{
		// 'restart' and 'required' are separate terms split by '-'.
		Ids("events | where Message has 'restart'").Should().BeEquivalentTo([3L]);
		Ids("events | where Message has 'required'").Should().BeEquivalentTo([3L]);
		// 'start' is a term in "starting up"? No — "starting" is the term. So no match.
		Ids("events | where Message has 'start'").Should().BeEmpty();
	}

	[Fact]
	public void Has_IsCaseInsensitive_HasCsIsCaseSensitive()
	{
		Ids("events | where Message has 'boom'").Should().BeEquivalentTo([4L]);
		Ids("events | where Message has_cs 'boom'").Should().BeEmpty();
		Ids("events | where Message has_cs 'BOOM'").Should().BeEquivalentTo([4L]);
	}

	// `!contains` and friends are SEPARATE Kusto.Language kinds (NotContainsExpression, NotHasCsExpression, …),
	// not a negation NODE — so each positive spelling needs its own compiled arm. A forgotten twin surfaced
	// as `binary 'NotContainsExpression' not supported` on a plain `where Message !contains "rejected"`
	// (work kql-negated-string-operators-unsupported), which is why every spelling is pinned here against
	// the COMPLEMENT of its positive form on all active backends: no null operand exists in the seed, so
	// Kusto's null rule makes the two sets exactly disjoint and jointly exhaustive.
	[Theory]
	[InlineData("contains 'art'", "!contains 'art'")]
	[InlineData("contains 'ART'", "!contains 'ART'")]
	[InlineData("contains_cs 'Earth'", "!contains_cs 'Earth'")]
	[InlineData("startswith 'crash'", "!startswith 'crash'")]
	[InlineData("startswith_cs 'crash'", "!startswith_cs 'crash'")]
	[InlineData("endswith 'normalized'", "!endswith 'normalized'")]
	[InlineData("endswith_cs 'normalized'", "!endswith_cs 'normalized'")]
	[InlineData("has 'restart'", "!has 'restart'")]
	[InlineData("has_cs 'BOOM'", "!has_cs 'BOOM'")]
	[InlineData("contains 'nothing-matches-this'", "!contains 'nothing-matches-this'")]
	public void NegatedStringOperators_AreTheExactComplement(string positive, string negated)
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var matching = KqlTestHost.Apply(Rows, Parse($"events | where Message {positive}"), backend)
				.Select(r => r.Id).ToList();
			var excluded = KqlTestHost.Apply(Rows, Parse($"events | where Message {negated}"), backend)
				.Select(r => r.Id).ToList();

			excluded.Should().BeEquivalentTo(
				Rows.Select(r => r.Id).Except(matching),
				$"'!{positive}' on {backend} must return exactly what '{positive}' does not");
		}
	}

	// An empty `has` term matches NOTHING in Kusto (it is not an empty-substring match), so its negation
	// matches every non-null value — a compile-time constant in HasFn, and the one arm where a naive
	// `Expr.Not` of that constant would also have to stay null-aware.
	[Fact]
	public void NegatedHas_WithEmptyTerm_MatchesEverything()
	{
		Ids("events | where Message has ''").Should().BeEmpty();
		Ids("events | where Message !has ''").Should().BeEquivalentTo([1L, 2L, 3L, 4L]);
	}

	// The reported repro, verbatim: double-quoted term, negated contains, on the ACTIVE backend.
	[Fact]
	public void NotContains_ReportedQuery_ReturnsTheNonMatchingRows()
	{
		Ids("events | where Message !contains \"crash\"").Should().BeEquivalentTo([1L, 3L, 4L]);
	}

	// A null operand is excluded from BOTH the positive and the negated spelling (Kusto null rule). The
	// has family does NOT get that for free: it lowers to MatchesRegex, which PINS a null subject to FALSE
	// in SQL (IFNULL/COALESCE), so a bare NOT(...) would flip it to TRUE and KEEP the null row — which is
	// why Negated() AND-s in an explicit IS NOT NULL. Row 2 carries no `env` property, so json_extract
	// yields NULL for it and it must appear in NEITHER list.
	[Fact]
	public void NegatedStringOperators_ExcludeNullOperands()
	{
		LogEntryRecord[] rows =
		[
			new() { Id = 1, Message = "dev row", PropertiesJson = """{"env":"dev"}""" },
			new() { Id = 2, Message = "no env property", PropertiesJson = """{"other":"x"}""" },
			new() { Id = 3, Message = "prod row", PropertiesJson = """{"env":"prod"}""" },
		];

		(string Predicate, long[] Expected)[] cases =
		[
			("has 'prod'", [3]),
			("!has 'prod'", [1]),
			("!has 'dev'", [3]),
			("!has_cs 'prod'", [1]),
			("!contains 'prod'", [1]),
			("!contains_cs 'prod'", [1]),
			("!startswith 'prod'", [1]),
			("!endswith 'prod'", [1]),
			("!has ''", [1, 3]),
		];

		foreach (var backend in KqlBackendConfig.Active)
			foreach (var (predicate, expected) in cases)
			{
				var kql = $"events | where Properties[\"env\"] {predicate}";
				KqlTestHost.Apply(rows, Parse(kql), backend).Select(r => r.Id).ToList()
					.Should().BeEquivalentTo(expected,
						$"'Properties[\"env\"] {predicate}' on {backend} must return exactly {string.Join(",", expected)} — the null-env row is excluded from both spellings");
			}
	}

	// `in~` / `!in~` are Kusto's CASE-INSENSITIVE membership spellings (Kusto.Language InCsExpression /
	// NotInCsExpression — a `Cs` that means the OPPOSITE of the one in `contains_cs`). They used to compile to a
	// plain ordinal equality, so the whole operator silently returned the wrong rows with no error:
	// `Message in~ ('crash on earth')` gave [] instead of [2]. Pinned on every active backend, because the fold
	// is now real SQL (`lower(...)` on both sides), not an in-memory nicety.
	[Theory]
	[InlineData("crash on earth")]
	[InlineData("CRASH ON EARTH")]
	[InlineData("Crash On Earth")]
	public void InTilde_IsCaseInsensitive_AndItsNegationIsTheComplement(string term)
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var matching = KqlTestHost.Apply(Rows, Parse($"events | where Message in~ ('{term}')"), backend)
				.Select(r => r.Id).ToList();
			var excluded = KqlTestHost.Apply(Rows, Parse($"events | where Message !in~ ('{term}')"), backend)
				.Select(r => r.Id).ToList();

			matching.Should().BeEquivalentTo([2L], $"'in~ ({term})' on {backend} must match row 2 whatever its casing");
			excluded.Should().BeEquivalentTo([1L, 3L, 4L], $"'!in~ ({term})' on {backend} must exclude only row 2");
		}
	}

	// The complement rule over a MULTI-element list (so the OR-chain, not just a single equality, is exercised):
	// no seed row is null, so the two spellings are disjoint and jointly exhaustive.
	[Fact]
	public void InTilde_ListIsTheExactComplementOfItsNegation()
	{
		const string list = "('CRASH ON EARTH', 'starting UP', 'nothing-matches-this')";
		foreach (var backend in KqlBackendConfig.Active)
		{
			var matching = KqlTestHost.Apply(Rows, Parse($"events | where Message in~ {list}"), backend)
				.Select(r => r.Id).ToList();
			var excluded = KqlTestHost.Apply(Rows, Parse($"events | where Message !in~ {list}"), backend)
				.Select(r => r.Id).ToList();

			matching.Should().BeEquivalentTo([1L, 2L], $"'in~ {list}' on {backend} must be case-insensitive");
			excluded.Should().BeEquivalentTo(
				Rows.Select(r => r.Id).Except(matching),
				$"'!in~ {list}' on {backend} must return exactly what 'in~ {list}' does not");
		}
	}

	// The tilde is what buys the case folding — `in` / `!in` stay ORDINAL, and this test is the guard that
	// keeps the fix from being "improved" into folding the plain spellings too.
	[Fact]
	public void In_StaysCaseSensitive()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			KqlTestHost.Apply(Rows, Parse("events | where Message in ('crash on earth')"), backend)
				.Select(r => r.Id).ToList().Should().BeEmpty($"'in' on {backend} is ordinal");
			KqlTestHost.Apply(Rows, Parse("events | where Message in ('crash on Earth')"), backend)
				.Select(r => r.Id).ToList().Should().BeEquivalentTo([2L], $"'in' on {backend} matches exact casing");
			KqlTestHost.Apply(Rows, Parse("events | where Message !in ('crash on Earth')"), backend)
				.Select(r => r.Id).ToList().Should().BeEquivalentTo([1L, 3L, 4L], $"'!in' on {backend} is ordinal");
		}
	}

	// A null left operand is excluded from BOTH spellings — `in~` folds the operand with lower(), and the
	// json_extract NULL must survive that fold as NULL rather than becoming a match. Row 2 carries no `env`
	// property, so it belongs in NEITHER list (same seeding style as NegatedStringOperators_ExcludeNullOperands).
	[Fact]
	public void InTilde_ExcludesNullOperands()
	{
		LogEntryRecord[] rows =
		[
			new() { Id = 1, Message = "dev row", PropertiesJson = """{"env":"dev"}""" },
			new() { Id = 2, Message = "no env property", PropertiesJson = """{"other":"x"}""" },
			new() { Id = 3, Message = "prod row", PropertiesJson = """{"env":"prod"}""" },
		];

		(string Predicate, long[] Expected)[] cases =
		[
			("in~ ('PROD')", [3]),
			("!in~ ('PROD')", [1]),
			("in~ ('nothing-matches-this')", []),
			("!in~ ('nothing-matches-this')", [1, 3]),
		];

		foreach (var backend in KqlBackendConfig.Active)
			foreach (var (predicate, expected) in cases)
			{
				var kql = $"events | where Properties[\"env\"] {predicate}";
				KqlTestHost.Apply(rows, Parse(kql), backend).Select(r => r.Id).ToList()
					.Should().BeEquivalentTo(expected,
						$"'Properties[\"env\"] {predicate}' on {backend} must return exactly [{string.Join(",", expected)}] — the null-env row is excluded from both spellings");
			}
	}

	// `!in` / `!between` parse to their own kinds and were wired by reading Kind inside In()/Between();
	// `not(...)` is a function. Before this fix NEITHER route could negate a string predicate other than
	// through not(), so pin that a negated spelling resolves WITHOUT falling into the generic
	// "binary '<Kind>' not supported" arm.
	[Theory]
	[InlineData("!contains 'a'")]
	[InlineData("!contains_cs 'a'")]
	[InlineData("!startswith 'a'")]
	[InlineData("!startswith_cs 'a'")]
	[InlineData("!endswith 'a'")]
	[InlineData("!endswith_cs 'a'")]
	[InlineData("!has 'a'")]
	[InlineData("!has_cs 'a'")]
	public void NegatedStringOperators_DoNotThrowUnsupported(string predicate)
	{
		var act = () => KqlTestHost.Apply(Rows, Parse($"events | where Message {predicate}"), KqlBackend.Sqlite);
		act.Should().NotThrow<UnsupportedKqlException>();
	}

	[Fact]
	public void MatchesRegex_Filters()
	{
		Ids("events | where Message matches regex '^crash'").Should().BeEquivalentTo([2L]);
		Ids("events | where Message matches regex 'art(ing|-)'").Should().BeEquivalentTo([1L, 3L]);
		Ids("events | where Message matches regex '[0-9]'").Should().BeEmpty();
	}

	[Fact]
	public void StartsEndsWith_CaseSensitivityAndBounds()
	{
		Ids("events | where Message startswith 'CRASH'").Should().BeEquivalentTo([2L]);
		Ids("events | where Message startswith_cs 'CRASH'").Should().BeEmpty();
		Ids("events | where Message endswith 'NORMALIZED'").Should().BeEquivalentTo([4L]);
		// needle longer than any value → no match, no exception.
		Ids("events | where Message startswith 'this is way too long to be a prefix'").Should().BeEmpty();
	}

	[Fact]
	public async Task Tolower_Toupper_Substring_Strcat_Compute()
	{
		var lower = await Table("events | where Id == 2 | project X = tolower(Message)");
		lower[0][0].Should().Be("crash on earth");

		var upper = await Table("events | where Id == 2 | project X = toupper(Message)");
		upper[0][0].Should().Be("CRASH ON EARTH");

		var sub2 = await Table("events | where Id == 2 | project X = substring(Message, 6)");
		sub2[0][0].Should().Be("on Earth");

		var sub3 = await Table("events | where Id == 2 | project X = substring(Message, 0, 5)");
		sub3[0][0].Should().Be("crash");

		var cat = await Table("events | where Id == 2 | project X = strcat(ServiceKey, ': ', Message)");
		cat[0][0].Should().Be("svc-b: crash on Earth");
	}

	[Fact]
	public async Task Extract_ReturnsGroupOrEmpty()
	{
		var hit = await Table("events | where Id == 3 | project X = extract('([a-z]+)-([a-z]+)', 2, Message)");
		hit[0][0].Should().Be("required");

		var miss = await Table("events | where Id == 1 | project X = extract('([0-9]+)', 1, Message)");
		miss[0][0].Should().Be(""); // Kusto: empty string when the regex does not match
	}

	[Fact]
	public async Task ParseJson_IsStringPassthrough()
	{
		// Dynamic values are not modeled — parse_json returns its input string unchanged.
		var rows = await Table("events | where Id == 1 | project X = parse_json('{\"a\":1}')");
		rows[0][0].Should().Be("{\"a\":1}");
	}

	[Theory]
	[InlineData("events | where Message startswith 3", "*startswith*string literal*")]
	[InlineData("events | project X = tolower(Message, 'extra')", "*tolower*1 argument*")]
	[InlineData("events | project X = substring(Message)", "*substring*2 or 3*")]
	[InlineData("events | project X = substring(Message, 'x')", "*integer*")]
	[InlineData("events | project X = extract('re', 1)", "*extract*3 arguments*")]
	[InlineData("events | project X = strcat(Message, Level)", "*strcat*string*")]
	[InlineData("events | where tolower(Level) == 'x'", "*tolower*string*")]
	public async Task InvalidStringCalls_ThrowPrecise(string kql, string message)
	{
		// force pipeline construction for both Apply-shaped and Execute-shaped queries; ExecuteAsync
		// surfaces the eager pipeline-build throw once awaited.
		var act = async () => await KqlTestHost.ExecuteAsync(Rows, Parse(kql), KqlBackend.Sqlite);
		(await act.Should().ThrowAsync<UnsupportedKqlException>()).WithMessage(message);
	}
}
