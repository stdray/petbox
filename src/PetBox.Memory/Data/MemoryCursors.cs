namespace PetBox.Memory.Data;

// Names of the durable Class-B index cursors living in a project's memory file (search_cursor /
// search_deadletter, keyed by IndexName). All of a project's stores now share ONE file, so a
// cursor name must carry the store it tracks — each store is an independent temporal partition
// with its own version cursor (exactly like the tasks tier, whose vector cursor IS the board name).
//
// The file also holds the background jobs' own markers (behavior-pattern mining, dedup sweep), so
// the vector cursors are namespaced under a `vector:` prefix rather than being the bare store name.
public static class MemoryCursors
{
	// Pre-merge (one file per store) the vector cursor needed no store in its name. Kept so the
	// merge migration can find and rewrite the legacy rows.
	public const string LegacyVector = "vector";

	// The vector-index cursor for one store within the project file.
	public static string Vector(string store) => $"vector:{store}";

	// The lexical PROJECTION version marker for one store (reindex-as-first-class-mechanism):
	// what schema version's search_fts rows this store currently carries, not a Class-B replay
	// cursor. MemoryService.EnsureLexicalBackfillAsync compares it against
	// MemorySearchDocs.LexicalProjectionVersion and rebuilds the store's search_fts on a mismatch —
	// a missing row reads as version 0, which is always behind version 1+, so a never-backfilled
	// store rebuilds exactly like a stale-projection one.
	public static string Lexical(string store) => $"lexical:{store}";
}
