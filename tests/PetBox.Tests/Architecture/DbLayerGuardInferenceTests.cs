using PetBox.Core.Data;

namespace PetBox.Tests.Architecture;

// PROOF for work `arch-gates-scope-declared-not-inferred`: DbLayerGuardTests.GuardedFactories used
// to be a hand-listed `Type[] { ICoreDbFactory, IDeployDbFactory, IScopedDbFactory<> }`. A typed
// facade written OVER one of those (exactly the shape `IConfigDbFactory` is) was invisible to it —
// not because the mechanism couldn't see the facade's members, but because the facade's OWN
// interface type was never in the array and matched neither the exact-type nor the
// open-generic-definition check.
//
// This file builds that exact shape as a FIXTURE — in the test tree, not product code, so no real
// violation is ever introduced — and runs BOTH forms (today's shape-based `IsGuarded`, and the old
// enumeration recreated verbatim below) against the identical fixture in the same test run. Same
// code path (`GuardedMembersOf`, which is what `Offenders()` — and so
// `NoPresentationType_TakesADbFactory` — actually calls), two different verdicts.
public sealed class DbLayerGuardInferenceTests
{
	// A fictitious facade over a REAL guarded factory (IScopedDbFactory<TasksDb>) — structurally
	// identical to PetBox.Config.Data.IConfigDbFactory over IScopedDbFactory<ConfigDb>: a narrow
	// interface whose only job is handing back a live connection, implemented by a class that holds
	// the underlying scoped factory in its constructor.
	public interface IFakeTypedDbFacade
	{
		PetBox.Tasks.Data.TasksDb OpenFake();
	}

	sealed class FakeTypedDbFacade(IScopedDbFactory<PetBox.Tasks.Data.TasksDb> inner) : IFakeTypedDbFacade
	{
		public PetBox.Tasks.Data.TasksDb OpenFake() => inner.GetDb("fake-scope");
	}

	// A fictitious presentation-shaped host — stands in for a Razor PageModel or MCP endpoint that
	// took the facade instead of asking a service, i.e. the exact violation
	// `NoPresentationType_TakesADbFactory` exists to catch. Deliberately does NOT store the
	// parameter in a field (an auto-property or captured primary-ctor field would ALSO match
	// GuardedMembersOf's field sweep, muddying this test's count) — only the ctor parameter itself
	// is meant to be the single observed hit here.
	sealed class FakePageHoldingTheFacade
	{
		public FakePageHoldingTheFacade(IFakeTypedDbFacade facade) => GC.KeepAlive(facade);
	}

	// The OLD mechanism, recreated verbatim (not imported — the point is to show what the array-based
	// form actually did, side by side with what replaced it). This is deliberately dead code except
	// for this one test: it must never be reintroduced as the real gate.
	static readonly Type[] OldGuardedFactories =
	[
		typeof(ICoreDbFactory),
		typeof(PetBox.Deploy.Data.IDeployDbFactory),
		typeof(IScopedDbFactory<>),
	];

	static bool OldIsGuarded(Type t) =>
		OldGuardedFactories.Contains(t)
		|| (t.IsGenericType && OldGuardedFactories.Contains(t.GetGenericTypeDefinition()));

	[Fact]
	public void NewInference_SeesTheFacadeItself_OldEnumerationDidNot()
	{
		DbLayerGuardTests.IsGuarded(typeof(IFakeTypedDbFacade)).Should().BeTrue(
			"the facade hands back TasksDb (a DataConnection) from OpenFake() — that shape alone makes "
			+ "it a door under the new inference, exactly the way IConfigDbFactory.GetConfigDb(): "
			+ "ConfigDb does");

		OldIsGuarded(typeof(IFakeTypedDbFacade)).Should().BeFalse(
			"the old enumeration named exactly three types; IFakeTypedDbFacade is none of them and is "
			+ "not IScopedDbFactory<>'s open generic definition either, so the old form had no way to "
			+ "know this facade existed — this is the literal blind spot real IConfigDbFactory sat in");
	}

	[Fact]
	public void NewInference_CatchesAPresentationTypeHoldingTheFacade_OldEnumerationDidNot()
	{
		// This is the same call Offenders() makes on every swept type — the mechanism actually behind
		// NoPresentationType_TakesADbFactory, not a re-derivation of it.
		var newMembers = DbLayerGuardTests.GuardedMembersOf(typeof(FakePageHoldingTheFacade)).ToList();

		newMembers.Should().ContainSingle(
			"the new shape-based IsGuarded flags the ctor parameter of type IFakeTypedDbFacade")
			.Which.Should().Be(".ctor(IFakeTypedDbFacade facade)");

		// The old member sweep, run with OldIsGuarded standing in for IsGuarded — same ctor-scan
		// shape as GuardedMembersOf, applied to the identical fixture type.
		var oldMembers = typeof(FakePageHoldingTheFacade)
			.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
			.SelectMany(c => c.GetParameters())
			.Where(p => OldIsGuarded(p.ParameterType))
			.ToList();

		oldMembers.Should().BeEmpty(
			"under the enumerated form this ctor parameter is invisible — a presentation type holding "
			+ "this exact facade would have sailed through NoPresentationType_TakesADbFactory green, "
			+ "which is precisely the gap real IConfigDbFactory sat in until this fix");
	}
}
