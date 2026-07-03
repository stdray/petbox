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

	static IReadOnlyList<long> Ids(string kql) =>
		KqlTransformer.Apply(Rows.AsQueryable(), Parse(kql)).ToList().Select(r => r.Id).ToList();

	static async Task<List<object?[]>> Table(string kql)
	{
		var result = KqlTransformer.Execute(Rows.AsQueryable(), Parse(kql));
		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
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
	public void InvalidStringCalls_ThrowPrecise(string kql, string message)
	{
		var act = () =>
		{
			var result = KqlTransformer.Execute(Rows.AsQueryable(), Parse(kql));
			// force pipeline construction for both Apply-shaped and Execute-shaped queries
			_ = result.Columns;
		};
		act.Should().Throw<UnsupportedKqlException>().WithMessage(message);
	}
}
