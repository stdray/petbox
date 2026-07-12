using LinqToDB;
using LinqToDB.Data;

namespace PetBox.Core.Data;

// Hands out FRESH, caller-owned connections to the Core db (petbox.db). The caller disposes.
//
// WHY THIS EXISTS. A linq2db DataConnection is NOT thread-safe. PetBoxDb is registered AddScoped —
// ONE connection per HTTP request — and any request that fans work out in parallel therefore has
// several threads driving ONE connection. That is not a hypothetical: CrossScopeTaskSearchService
// runs up to MaxProjectConcurrency per-project searches at once within a single request scope, and
// the overlapping calls trampled the shared SqliteCommand's parameter list — prod 500'd with
// "Must add values for the following parameters: @projectKey, @board", "Collection was modified",
// ObjectDisposedException: one race, several faces.
//
// The fix that STICKS is not "remember to clone the connection at each call site" — it is removing
// the shared connection from reach. Take an ICoreDbFactory and `using var db = factory.Open()` per
// call, and the bug class is unrepresentable: there is no long-lived connection to share.
//
// This is deliberately NOT IScopedDbFactory<T>. That one is scope-KEYED (a file per project /
// workspace, schema ensured lazily per file); core.db is a SINGLE file whose schema is built up
// front by MigrationRunner. Same intent, different shape.
public interface ICoreDbFactory
{
	// A fresh, caller-owned connection to core.db. Never share it across threads; dispose it.
	PetBoxDb Open();
}

// Registered as a SINGLETON: it holds no connection, only the immutable DataOptions describing how
// to make one. (A scoped factory would be pointless — the thing we are eliminating is exactly the
// per-scope shared connection.)
public sealed class CoreDbFactory : ICoreDbFactory
{
	// Built ONCE. This is load-bearing for memory, not just speed: PetBoxDb.CreateOptions attaches
	// the SHARED MappingSchema (PetBoxDb.SharedMappingSchema). Building a MappingSchema per
	// connection instead makes linq2db's per-schema MappingAttributesCache grow without bound —
	// ~290 MB / 3M+ nodes by mid-day, which is what drove the production OOM. Any future edit here
	// MUST keep going through CreateOptions (or clone existing DataOptions); never hand a
	// PetBoxDb a fresh MappingSchema.
	readonly DataOptions<PetBoxDb> _options;

	public CoreDbFactory(string connectionString)
		: this(PetBoxDb.CreateOptions(connectionString)) { }

	// Clones the supplied options, which preserves the provider, the connection string AND the
	// shared mapping schema.
	public CoreDbFactory(DataOptions<PetBoxDb> options) =>
		_options = new DataOptions<PetBoxDb>(options.Options);

	public PetBoxDb Open() => new(_options);
}
