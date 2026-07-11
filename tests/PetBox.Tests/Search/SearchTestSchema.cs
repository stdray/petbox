using PetBox.Memory.Data;

namespace PetBox.Tests.Search;

// Schema for the contract-level search tests (SearchService / SqliteFtsIndex / VectorSearchIndex),
// which exercise the indexes over a bare temp .db that belongs to no tier.
//
// The indexes no longer carry an EnsureSchema: search_fts / search_vec / search_cursor /
// search_deadletter are DDL, and DDL is born in exactly ONE place — a migration. So a test that
// needs those tables runs a real tier's migration set instead of hand-rolling CREATE TABLE (which
// would resurrect the second source of truth these tests exist to protect). The memory tier is
// the one picked here because its M006_SearchTables defines all four contract tables; the extra
// memory tables it brings along are inert for these tests, and M010's legacy merge is a no-op in
// a fresh temp directory.
static class SearchTestSchema
{
	public static void Ensure(string connectionString) => MemorySchema.Ensure(connectionString);
}
