using Kusto.Language;
using Kusto.Language.Editor;

namespace PetBox.Tests.Kql;

// The ROUND TRIP between the compiler and the completer, in both directions:
//
//   offer → compile   every operator/function KqlCompletionService advertises is compiled by the
//                     production path for real (no UnsupportedKqlException);
//   compile → offer   every operator the engine supports is advertised, and the allowlist has no entry
//                     Kusto.Language never offers (no dead capability claims).
//
// The single source of truth is KqlScalar.SupportedInfixOperators / KqlScalarFunctions: the completer
// asks the compiler, and this file proves the answer against the live compile path. A Kusto.Language
// upgrade that starts offering a new operator fails here (it would land in `offered` but not in the
// allowlist) instead of silently disappearing from the UI or silently staying unsupported.
public sealed class KqlOperatorCapabilityTests
{
	static readonly LogEntryRecord[] Rows =
	[
		new() { Id = 1, Message = "starting up", Level = (int)LogLevel.Information },
		new() { Id = 2, Message = "crash on Earth", Level = (int)LogLevel.Error },
		new() { Id = 3, Message = "restart-required", Level = (int)LogLevel.Warning },
		new() { Id = 4, Message = "BOOM normalized", Level = (int)LogLevel.Information },
	];

	// One `where` predicate per operator the engine supports, with the rows it must return — so each entry
	// is proved to COMPILE and to mean what its name says, not merely to exist. `null` rows = compile-only
	// (in~ / !in~ case folding is owned by the parallel fix/kql-in-tilde-case-folding branch, which this
	// branch must not pre-empt: the row set changes there, the compilability does not).
	static readonly (string Operator, string Predicate, long[]? Expected)[] Operators =
	[
		("and", "Message has 'crash' and Message has 'earth'", [2]),
		("or", "Message has 'crash' or Message has 'restart'", [2, 3]),
		("+", "Id + 2 > 4", [3, 4]),
		("-", "Id - 1 > 2", [4]),
		("*", "Id * 2 > 4", [3, 4]),
		("/", "Id / 2 == 1", [2, 3]),
		("%", "Id % 2 == 0", [2, 4]),
		("==", "Message == 'crash on Earth'", [2]),
		("!=", "Message != 'crash on Earth'", [1, 3, 4]),
		("<", "Id < 2", [1]),
		("<=", "Id <= 2", [1, 2]),
		(">", "Id > 2", [3, 4]),
		(">=", "Id >= 2", [2, 3, 4]),
		("=~", "Message =~ 'CRASH ON EARTH'", [2]),
		("!~", "Message !~ 'CRASH ON EARTH'", [1, 3, 4]),
		("in", "Id in (1, 3)", [1, 3]),
		("!in", "Id !in (1, 3)", [2, 4]),
		("in~", "Message in~ ('CRASH ON EARTH')", null),
		("!in~", "Message !in~ ('CRASH ON EARTH')", null),
		("between", "Id between (2 .. 3)", [2, 3]),
		("!between", "Id !between (2 .. 3)", [1, 4]),
		("contains", "Message contains 'rash'", [2]),
		("!contains", "Message !contains 'rash'", [1, 3, 4]),
		("contains_cs", "Message contains_cs 'Earth'", [2]),
		("!contains_cs", "Message !contains_cs 'Earth'", [1, 3, 4]),
		("startswith", "Message startswith 'crash'", [2]),
		("!startswith", "Message !startswith 'crash'", [1, 3, 4]),
		("startswith_cs", "Message startswith_cs 'crash'", [2]),
		("!startswith_cs", "Message !startswith_cs 'crash'", [1, 3, 4]),
		("endswith", "Message endswith 'normalized'", [4]),
		("!endswith", "Message !endswith 'normalized'", [1, 2, 3]),
		("endswith_cs", "Message endswith_cs 'normalized'", [4]),
		("!endswith_cs", "Message !endswith_cs 'normalized'", [1, 2, 3]),
		("has", "Message has 'crash'", [2]),
		("!has", "Message !has 'crash'", [1, 3, 4]),
		("has_cs", "Message has_cs 'BOOM'", [4]),
		("!has_cs", "Message !has_cs 'BOOM'", [1, 2, 3]),
		("hasprefix", "Message hasprefix 'nor'", [4]),
		("!hasprefix", "Message !hasprefix 'nor'", [1, 2, 3]),
		("hasprefix_cs", "Message hasprefix_cs 'norm'", [4]),
		("!hasprefix_cs", "Message !hasprefix_cs 'norm'", [1, 2, 3]),
		("hassuffix", "Message hassuffix 'TING'", [1]),
		("!hassuffix", "Message !hassuffix 'TING'", [2, 3, 4]),
		("hassuffix_cs", "Message hassuffix_cs 'ting'", [1]),
		("!hassuffix_cs", "Message !hassuffix_cs 'ting'", [2, 3, 4]),
		("has_any", "Message has_any ('crash', 'boom')", [2, 4]),
		("has_all", "Message has_all ('crash', 'earth')", [2]),
		("matches regex", "Message matches regex '^crash'", [2]),
	];

	// Probe positions whose operand TYPE decides which infix operators Kusto.Language offers (string →
	// the string family, numeric → the ordering family, bool → and/or, a numeric literal → arithmetic).
	// Their union is the full operator surface the completer can ever be asked for.
	static readonly string[] Probes =
	[
		"events | where Message ",
		"events | where Id ",
		"events | where (Id > 1) ",
		"events | extend X = 1 ",
	];

	static List<string> OfferedOperators(string probe)
	{
		var service = new KustoCodeService(probe, KqlSchema.Globals);
		return service.GetCompletionItems(probe.Length).Items
			.Where(i => i.Kind == CompletionKind.ScalarInfix)
			.Select(i => i.DisplayText)
			.Distinct(StringComparer.Ordinal)
			.ToList();
	}

	static List<string> CompletedKinds(string probe, CompletionKind kind)
	{
		var result = KqlCompletionService.Complete(probe, probe.Length);
		// The service caps its answer at MaxItems AFTER ordering; a longer candidate list would make the
		// "nothing was filtered out" comparison below meaningless, so say so loudly instead of passing.
		result.Items.Should().HaveCountLessThan(KqlCompletionService.MaxItems,
			$"probe '{probe}' must fit under the completion cap for this comparison to mean anything");
		return result.Items
			.Where(i => i.Kind == kind.ToString())
			.Select(i => i.DisplayText)
			.ToList();
	}

	// Direction 1: everything Kusto.Language offers goes through the production compile path. The predicate
	// table above is keyed by the SAME DisplayTexts, so this covers every offered item one-for-one.
	[Fact]
	public void EverySupportedOperator_Compiles_AndMeansWhatItSays()
	{
		foreach (var backend in KqlBackendConfig.Active)
			foreach (var (op, predicate, expected) in Operators)
			{
				var act = () => KqlTestHost.Apply(Rows, KustoCode.Parse($"events | where {predicate}"), backend);
				act.Should().NotThrow<UnsupportedKqlException>(
					$"operator '{op}' is in the allowlist, so it must compile on {backend}");

				if (expected is not null)
					act().Select(r => r.Id).ToList().Should().BeEquivalentTo(expected,
						$"'{predicate}' ({op}) on {backend} must return {string.Join(",", expected)}");
			}
	}

	// Direction 2a: the completer never hides an operator the engine supports — at every probe the offered
	// set is EXACTLY what Kusto.Language offers (after this branch, every offered operator compiles, so a
	// shorter answer would mean the capability filter is over-filtering).
	[Fact]
	public void Completer_Offers_EveryOperatorKustoLanguageOffers()
	{
		foreach (var probe in Probes)
		{
			var offered = OfferedOperators(probe);
			offered.Should().NotBeEmpty($"probe '{probe}' is expected to sit in an infix-operator position");
			CompletedKinds(probe, CompletionKind.ScalarInfix).Should().BeEquivalentTo(offered,
				$"'{probe}' must offer every operator Kusto.Language offers — each of them compiles");
		}
	}

	// Direction 2b/3: the allowlist and the live offered surface are the same set — no dead entries in the
	// manifest (an operator the engine claims but Kusto.Language never offers) and no untriaged operator
	// (Kusto.Language offering something absent from the manifest: implement it, or filter it deliberately).
	[Fact]
	public void OperatorAllowlist_Equals_TheOfferedSurface()
	{
		var offered = Probes.SelectMany(OfferedOperators).Distinct(StringComparer.Ordinal).ToList();
		var supported = KqlScalar.SupportedInfixOperators.ToList();

		offered.Except(supported, StringComparer.Ordinal).Should().BeEmpty(
			"every operator Kusto.Language offers must be supported (or the completer would hide a working operator)");
		supported.Except(offered, StringComparer.Ordinal).Should().BeEmpty(
			"every supported operator must be one Kusto.Language actually offers — do not claim capabilities the completer can never use");

		Operators.Select(o => o.Operator).Should().BeEquivalentTo(supported,
			"the predicate table must exercise every allowlisted operator, one-for-one");
	}

	// --- built-in functions: the same contract, answered by KqlScalarFunctions ---
	//
	// Probed by PREFIX (`extend X = tostr`), not at a bare expression position: a bare position offers
	// hundreds of built-ins and the service's MaxItems cap would truncate the answer for a reason that has
	// nothing to do with capability. Each prefix below keeps the candidate list small and still covers both
	// halves — registered names that must survive, unregistered names that must not.

	[Theory]
	[InlineData("events | extend X = tostr")]
	[InlineData("events | extend X = toint")]
	[InlineData("events | extend X = is")]
	[InlineData("events | extend X = str")]
	[InlineData("events | extend X = array")]
	public void Completer_Offers_ExactlyTheRegisteredBuiltInFunctions(string probe)
	{
		var service = new KustoCodeService(probe, KqlSchema.Globals);
		var offered = service.GetCompletionItems(probe.Length).Items
			.Where(i => i.Kind == CompletionKind.BuiltInFunction)
			.Select(i => i.DisplayText)
			.Distinct(StringComparer.Ordinal)
			.ToList();
		offered.Should().NotBeEmpty($"'{probe}' sits in a function-name position");

		// The expected answer IS the registry's: everything Kusto.Language offers whose name
		// KqlScalarFunctions resolves, narrowed to the probe's own prefix (Kusto.Language hands the service
		// the whole function catalog; the SERVICE applies the typed prefix, the filter only drops what the
		// compiler cannot compile). Equality — not containment — is what proves the filter neither keeps an
		// uncompilable function nor drops a registered one.
		var prefix = probe[(probe.LastIndexOf(' ') + 1)..];
		var expected = offered
			.Where(KqlScalar.CanCompileFunction)
			.Where(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			.ToList();
		CompletedKinds(probe, CompletionKind.BuiltInFunction).Should().BeEquivalentTo(expected,
			$"'{probe}' must offer exactly the built-ins KqlScalarFunctions can compile");
	}

	[Fact]
	public void Completer_Drops_UnregisteredBuiltInFunctions()
	{
		foreach (var supported in new[] { "tostring(value)", "tostring(", "isnull(" })
			KqlScalar.CanCompileFunction(supported).Should().BeTrue($"'{supported}' is in the scalar registry");

		foreach (var unsupported in new[]
			{ "strlen(string)", "array_length(array)", "bag_keys(object)", "series_sum(series)", "strcat_delim(delimiter, arg, ...)" })
			KqlScalar.CanCompileFunction(unsupported).Should().BeFalse($"'{unsupported}' has no KqlScalarFunctions entry");

		CompletedKinds("events | extend X = str", CompletionKind.BuiltInFunction).Should().Contain("strcat(arg, ...)");
		CompletedKinds("events | extend X = str", CompletionKind.BuiltInFunction).Should().NotContain("strlen(string)");
		CompletedKinds("events | extend X = array", CompletionKind.BuiltInFunction).Should().BeEmpty(
			"no array function is implemented, so the completer offers none of them");
	}

	// The function half of the round trip: a registered function that the completer advertises must compile
	// through the production path (the argument shapes below are the registry's own). `extend` is a
	// shape-changing stage, so it runs through Execute.
	[Theory]
	[InlineData("tostring(Level)")]
	[InlineData("tolower(Message)")]
	[InlineData("isnull(Properties[\"env\"])")]
	[InlineData("isempty(Message)")]
	[InlineData("bin(Level, 2)")]
	[InlineData("strcat(Message, 'x')")]
	public async Task AdvertisedFunctions_Compile(string call)
	{
		var act = async () => await KqlTestHost.ExecuteAsync(Rows,
			KustoCode.Parse($"events | extend Y = {call}"), KqlBackend.Sqlite);
		await act.Should().NotThrowAsync<UnsupportedKqlException>($"'{call}' is advertised by the completer");
	}
}
