using System.Collections;
using System.Reflection;
using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Data;

// THE regression test for work/linq2db-per-connection-options-leak — reads the ACTUAL leaking
// registry (LinqToDB.Internal.Common.IdentifierBuilder._objects / _identifiers) instead of a proxy
// for it, so it fails on the disease and not merely on a symptom that happens to correlate with it.
//
// Why a proxy is not enough: ScopedDbFactoryOptionsMemoTests (this directory) asserts that repeat
// opens of the same (scopeKey, name) share one DataOptions instance — necessary, but a future
// refactor could satisfy that while still triggering the actual interning (e.g. by rebuilding
// DataOptions' interceptor list on every query rather than every connection). This test instead
// reads the exact static dictionaries that grew unbounded in production: a gcdump from the
// diagnosing incident showed 773,133 live entries in `_objects` and 2,319,402 in `_identifiers` —
// a 3.0004 ratio — after ~13h at ~16 connections/sec, with linq2db's own `IdentifierBuilder`
// declaring a `ClearCache()` that is never called anywhere in this codebase (or by linq2db itself),
// so nothing ever evicts an entry once added.
//
// Lab reproduction of the same ratio: on the pre-fix ScopedDbFactory, 200 connections to ONE file
// (each running a real query — see below) grew `_objects` by exactly 200 and `_identifiers` by
// exactly 600 — 1 and 3 per connection, matching the production ratio. With the fix, both are 0 when
// this test runs alone.
//
// It does NOT run alone in the real gate: `_objects`/`_identifiers` are process-global, and xunit
// runs test collections in parallel (xunit.runner.json: parallelizeTestCollections=true) — every
// OTHER concurrently-running test that opens its own first-ever connection to its own distinct
// (temp-file, TContext) pair legitimately adds ITS one interceptor to the same global dictionaries,
// which is correct behaviour, not this disease. A strict `Be(0)` is flaky under the full suite for
// exactly that reason (observed +1 in a full `--target=Test` run). The disease this test exists to
// catch is GROWTH PROPORTIONAL TO REPEAT CONNECTIONS AGAINST ONE FILE — it reproduces at a strict
// 1:1 ratio with `connections` (200 for 200), two orders of magnitude above any plausible amount of
// unrelated-test noise in a sub-second window — so the assertion below bounds growth well below
// `connections` rather than at exactly zero.
public sealed class IdentifierBuilderGrowthProbeTests : IDisposable
{
	readonly List<string> _dirs = [];

	ScopedDbFactory<TasksDb> NewFactory()
	{
		var dir = Path.Combine(Path.GetTempPath(), $"idbuilder-probe-{Guid.NewGuid():N}");
		_dirs.Add(dir);
		return new ScopedDbFactory<TasksDb>(dir, Scope.Project,
			cs => new TasksDb(TasksDb.CreateOptions(cs)), TestSchema.Tasks);
	}

	public void Dispose()
	{
		foreach (var dir in _dirs) TestDirs.CleanupOrDefer(dir);
	}

	static int Count(string fieldName)
	{
		var asm = typeof(LinqToDB.Data.DataConnection).Assembly;
		var type = asm.GetType("LinqToDB.Internal.Common.IdentifierBuilder", throwOnError: true)!;
		var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)
			?? throw new InvalidOperationException($"no static field {fieldName} on {type}");
		var dict = field.GetValue(null) ?? throw new InvalidOperationException($"{fieldName} was null");
		return ((ICollection)dict).Count;
	}

	[Fact]
	public void OpeningManyConnections_DoesNotGrowIdentifierBuilderRegistries()
	{
		var factory = NewFactory();

		// Warm up: the first connection pays whatever one-time interning this TContext/MappingSchema
		// combination needs regardless of the fix. A real LINQ query is REQUIRED here — linq2db
		// interns configuration identifiers while BUILDING a query (compiling the SQL plan cache
		// key), not while merely constructing a DataConnection. A probe that only opens connections
		// without querying them passes vacuously on both the leaking and the fixed code.
		using (var warm = factory.NewEnsuredConnection("p")) { _ = warm.TaskNodes.Count(); }

		var objectsBefore = Count("_objects");
		var identifiersBefore = Count("_identifiers");

		const int connections = 200;
		for (var i = 0; i < connections; i++)
		{
			using var db = factory.NewEnsuredConnection("p");
			_ = db.TaskNodes.Count();
		}

		var objectsGrowth = Count("_objects") - objectsBefore;
		var identifiersGrowth = Count("_identifiers") - identifiersBefore;

		// On the leaking code this is +200 / +600 — one interceptor (and 3 interned strings) PER
		// CONNECTION, unbounded in the number of connections ever opened: growth == connections, and
		// growth == 3 * connections, exactly. The fix caps both at whatever unrelated concurrent
		// tests contribute (N-INDEPENDENT — it does not scale with `connections`), which is the
		// property that actually stops the prod OOM: past the first connection to a given
		// (scopeKey, name), no further identifier ever gets interned FOR THAT FILE. The bounds below
		// (connections / 4 objects, 3x that for identifiers) sit an order of magnitude above the
		// worst noise observed from the rest of the suite and two orders below what the disease
		// produces, so this still fails hard on a regression while tolerating a parallel gate.
		objectsGrowth.Should().BeLessThan(connections / 4,
			$"{connections} connections to ONE file must not grow linq2db's static IdentifierBuilder " +
			"._objects anywhere near linearly — a fresh DataOptions per connection is exactly what " +
			"interns a new ConnectionOptionsConnectionInterceptor forever " +
			"(work/linq2db-per-connection-options-leak); the small headroom above 0 absorbs unrelated " +
			"tests' own first-time interning running concurrently in the full suite");
		identifiersGrowth.Should().BeLessThan(3 * (connections / 4),
			$"{connections} connections to ONE file must not grow IdentifierBuilder._identifiers " +
			"anywhere near linearly either — it grows in lockstep with _objects (3 interned strings " +
			"per interned object in the production dump)");
	}
}
