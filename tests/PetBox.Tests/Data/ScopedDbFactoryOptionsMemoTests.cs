using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Data;

// Secondary regression coverage for work/linq2db-per-connection-options-leak, ONE LEVEL ABOVE the
// disease itself. The primary test is IdentifierBuilderGrowthProbeTests (this directory), which
// reads linq2db's actual leaking registry (LinqToDB.Internal.Common.IdentifierBuilder._objects /
// _identifiers) and proves querying does not grow it — that is the mechanism, and the only thing
// that fully rules out the leak.
//
// This file asserts the API-level CONTRACT the fix establishes to stop that mechanism: for one
// (scopeKey, name), every connection opened over the factory's lifetime shares the SAME DataOptions
// reference (the object the interceptor lives inside). It is necessary but NOT sufficient on its
// own — a future change could keep this contract while still re-triggering interning some other
// way (e.g. rebuilding an interceptor list on every query instead of every connection) — which is
// exactly why the growth-probe test exists and must not be deleted in favor of this one. Kept
// alongside it because it pins the DESIGN invariant (one DataOptions per file) independently of
// linq2db's internals, and fails with a much more direct message when that invariant regresses.
// TasksDb stands in for all five contexts that shared this bug (TasksDb, MemoryDb, SessionsDb,
// ConfigDb, LogDb) — they are wired identically through ScopedDbFactory<T>, so one is sufficient to
// pin the shared invariant without duplicating the same test five times.
public sealed class ScopedDbFactoryOptionsMemoTests : IDisposable
{
	readonly List<string> _dirs = [];

	ScopedDbFactory<TasksDb> NewFactory()
	{
		var dir = Path.Combine(Path.GetTempPath(), $"scoped-db-options-memo-{Guid.NewGuid():N}");
		_dirs.Add(dir);
		return new ScopedDbFactory<TasksDb>(dir, Scope.Project,
			cs => new TasksDb(TasksDb.CreateOptions(cs)), TestSchema.Tasks);
	}

	public void Dispose()
	{
		foreach (var dir in _dirs) TestDirs.CleanupOrDefer(dir);
	}

	[Fact]
	public void NewEnsuredConnection_ReturnsFreshCallerOwnedConnections_ButSharesOneDataOptions()
	{
		var factory = NewFactory();

		using var a = factory.NewEnsuredConnection("proj-a");
		using var b = factory.NewEnsuredConnection("proj-a");

		// spec: conn-safety-fresh-conn — the DataConnection itself must stay fresh and caller-owned.
		// This is the guard rail against "fixing" the leak by caching the connection instead of the
		// options, which this test suite would otherwise not catch.
		a.Should().NotBeSameAs(b, "every caller must get its own DataConnection — sharing the " +
			"connection itself would violate conn-safety-fresh-conn even though it also happens to " +
			"stop the leak");

		// The actual regression: on the leaking code, TasksDb.CreateOptions(cs) runs again on every
		// call, so `a.Options` and `b.Options` are two DIFFERENT objects — each one interning a new
		// ConnectionOptionsConnectionInterceptor in linq2db's static registry forever. After the fix,
		// both connections are built from the SAME cached DataOptions.
		a.Options.Should().BeSameAs(b.Options,
			"repeat opens of the same (scopeKey, name) must reuse one DataOptions instance — a fresh " +
			"DataOptions per connection is exactly what interns a new linq2db interceptor forever "
			+ "(work/linq2db-per-connection-options-leak)");
	}

	[Fact]
	public void NewEnsuredConnection_NCallsProduceExactlyOneDistinctDataOptionsInstance()
	{
		var factory = NewFactory();

		var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
		for (var i = 0; i < 25; i++)
		{
			using var conn = factory.NewEnsuredConnection("proj-a");
			seen.Add(conn.Options);
		}

		// On the leaking code this count is 25 (one new DataOptions — and interceptor — per call,
		// growing without bound as connection volume grows). The fix caps it at 1 regardless of N,
		// which is the actual property that stops the prod OOM: the leaked registries are keyed by
		// object identity, so "at most one object per (scopeKey, name) for the life of the process"
		// is what makes them bounded.
		seen.Should().HaveCount(1,
			"N opens of the same (scopeKey, name) must never produce more than one distinct " +
			"DataOptions instance — that count is exactly what the leaked linq2db registries grow by");
	}

	[Fact]
	public void NewEnsuredConnection_DifferentScopeKeys_GetDistinctDataOptions()
	{
		var factory = NewFactory();

		using var a = factory.NewEnsuredConnection("proj-a");
		using var b = factory.NewEnsuredConnection("proj-b");

		// The memoization must be keyed by (scopeKey, name) — a single global cache would silently
		// point every project's TasksDb at project-a's file.
		a.Options.Should().NotBeSameAs(b.Options,
			"different (scopeKey, name) pairs are different files and must not share DataOptions " +
			"(different connection strings)");
	}
}
