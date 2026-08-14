using PetBox.Core.Data;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Data;

// Regression coverage for work/linq2db-per-connection-options-leak — the prod OOM where
// ScopedDbFactory<T> rebuilt DataOptions (and therefore a brand-new
// LinqToDB.Interceptors.ConnectionOptionsConnectionInterceptor) on EVERY NewEnsuredConnection
// call. linq2db's static LinqToDB.Internal.Common.IdentifierBuilder._objects interns each such
// interceptor forever — 773k live interceptors / ~13h in the production dump that diagnosed this.
//
// The disease is "a fresh DataOptions per connection", not merely "memory grows" — a symptom-level
// test (e.g. watching process RSS) would not distinguish this from a dozen other possible leaks and
// would be slow/flaky. So this asserts the actual invariant the fix establishes: for one
// (scopeKey, name), every connection opened over the factory's lifetime shares the SAME DataOptions
// reference. TasksDb stands in for all five contexts that shared this bug (TasksDb, MemoryDb,
// SessionsDb, ConfigDb, LogDb) — they are wired identically through ScopedDbFactory<T>, so one is
// sufficient to pin the shared invariant without duplicating the same test five times.
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
