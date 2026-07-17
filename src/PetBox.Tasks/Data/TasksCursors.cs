namespace PetBox.Tasks.Data;

// Names of the durable index markers living in a project's tasks file (search_cursor, keyed by
// IndexName). The Class-B vector cursor of a board is the BARE board name (TasksVectorizationJob)
// — no prefix — so every OTHER marker in this file must be namespaced to avoid colliding with a
// board named the same as the marker; a board slug is `[a-z][a-z0-9_-]*` (no colon, ever), so a
// colon-prefixed name here can never collide with one.
public static class TasksCursors
{
	// The lexical PROJECTION version marker (reindex-as-first-class-mechanism): what schema
	// version's search_fts rows this project file currently carries — not a Class-B replay cursor.
	// One marker for the WHOLE file: nodes and their comments both come out of the single
	// TasksSearchDocs projection, so one version bump rebuilds both in the same backfill pass.
	// TasksService.EnsureLexicalBackfillAsync compares it against
	// TasksSearchDocs.LexicalProjectionVersion; SearchReindexService rewinds it to 0 to force a
	// rebuild on demand (search_reindex's Class-A half).
	public const string Lexical = "lexical:projection";

	// The META PROJECTION version marker (search-index-authority): what schema version's search_meta
	// facet/alias rows this project file currently carries — the exact same version-gated mechanism as
	// Lexical, but for the reference layer instead of the text floor. One marker for the WHOLE file
	// (all boards' nodes come out of the single TasksSearchDocs.ToMetaDoc projection).
	// TasksService.EnsureMetaBackfillAsync compares it against TasksSearchDocs.MetaProjectionVersion;
	// a fresh (empty) search_meta table reads as version 0 and rebuilds on the next search.
	public const string Meta = "meta:projection";
}
