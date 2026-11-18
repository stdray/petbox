using Kusto.Language;

namespace PetBox.Tests.Kql;

// work/kql-nan-infinity-constants: a NaN / ±Infinity constant has NO SQL literal. linq2db folds an all-constant
// expression itself at SQL-generation time and prints a double with double.ToString(), so `0.0/0.0` used to
// reach SQLite as the BARE IDENTIFIER `NaN` (`SQLite Error 1: 'no such column: NaN'`) and `1.0/0.0` as
// `Infinity` — a raw binder message where a KQL-level answer belongs. The engine now REFUSES such a constant at
// compile time (KqlScalar.NonFiniteReal) instead of folding the comparison, because a fold would have to pick
// one dialect's answer (SQLite holds no NaN at all; DuckDB holds one and orders `NaN = NaN` TRUE).
// Every case runs over KqlBackendConfig.Active: the refusal is deliberately the same on both.
public sealed class KqlNonFiniteConstantTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	static readonly LogEntryRecord[] Rows =
	[
		new() { Id = 1, Message = "2.5" },
		new() { Id = 2, Message = "nope" },
	];

	static void ShouldRefuse(string predicate, string message)
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var act = () => KqlTestHost.Apply(Rows, Parse($"events | where {predicate}"), backend);
			act.Should().Throw<UnsupportedKqlException>().WithMessage(message,
				$"a non-finite real constant must be refused on {backend}, not printed as an identifier");
		}
	}

	// The two shapes from the card, plus the sign and the nested fold that reach the same value. The message
	// names the VALUE and the supported alternative, so a user is not left with a binder error to decode.
	[Theory]
	[InlineData("todouble(Message) == 0.0/0.0", "'/' on constant operands: NaN has no SQL literal — use isnan()/isinf() to test a VALUE instead")]
	[InlineData("todouble(Message) != 0.0/0.0", "'/' on constant operands: NaN has no SQL literal — use isnan()/isinf() to test a VALUE instead")]
	[InlineData("todouble(Message) > 1.0/0.0", "'/' on constant operands: +Infinity has no SQL literal — use isnan()/isinf() to test a VALUE instead")]
	[InlineData("todouble(Message) == -1.0/0.0", "'/' on constant operands: -Infinity has no SQL literal — use isnan()/isinf() to test a VALUE instead")]
	[InlineData("todouble(Message) == (1.0-1.0)/0.0", "'/' on constant operands: NaN has no SQL literal — use isnan()/isinf() to test a VALUE instead")]
	[InlineData("todouble(Message) == 1e400", "this real literal overflows: +Infinity has no SQL literal — use isnan()/isinf() to test a VALUE instead")]
	public void NonFiniteConstant_IsRefusedAtCompileTime(string predicate, string message) =>
		ShouldRefuse(predicate, message);

	// The refusal covers the WHOLE arithmetic fold, not just a bare literal pair: `todouble(1)/todouble(0)`
	// folds through the typed conversion the same way a raw literal does.
	[Fact]
	public void NonFiniteConstant_ThroughATypedConversion_IsRefusedToo() =>
		ShouldRefuse("todouble(Message) == real(1)/real(0)",
			"'/' on constant operands: +Infinity has no SQL literal — use isnan()/isinf() to test a VALUE instead");

	// RFC: the FINITE folds must still work — the guard may not swallow ordinary constant arithmetic. `2.5` is
	// row 1's message and 10.0/4.0 is 2.5, so the row is found through real SQL (a mask would return nothing).
	[Fact]
	public void FiniteConstantArithmetic_StillRuns()
	{
		foreach (var backend in KqlBackendConfig.Active)
			KqlTestHost.Apply(Rows, Parse("events | where todouble(Message) == 10.0/4.0"), backend)
				.Select(r => r.Id).ToList().Should().BeEquivalentTo([1L], $"finite constant arithmetic is untouched on {backend}");
	}

	// A DIVISION BY ZERO IS NOT A CONSTANT FOLD when an operand is a column: it stays runtime SQL and keeps
	// whatever the dialect does with it — this test only pins that it is not refused at compile time.
	[Fact]
	public void DivisionByZeroWithAColumnOperand_IsNotRefused()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var act = () => KqlTestHost.Apply(Rows, Parse("events | where todouble(Message) / 0.0 > 1"), backend);
			act.Should().NotThrow($"a column operand is not a constant, so there is no literal to refuse on {backend}");
		}
	}
}
