using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// search-slug-words-gap: TasksSearchDocs.ToDoc now leads a node's indexed text with its SLUG, so
// the slug's words (`methodology`, `lifecycle`, `ux`) are lexical terms and an English query built
// from an English kebab slug can reach a Russian-titled node. But the DOCUMENTS already on disk
// were projected by the OLD ToDoc and carry no slug terms — a code-only change would fix new and
// re-upserted nodes and quietly leave every existing one in exactly the gap this closes.
//
// There is no other route back: `search_reindex` resets Class-B (vector cursors + dead-letter)
// only, and the Class-A lexical backfill is guarded by "search_fts has ANY row", so on a
// populated file it never re-runs. Emptying the table IS the reindex — the guard then sees a
// virgin index and TasksService.EnsureLexicalBackfillAsync rebuilds both passes (nodes, then
// comments) from the temporal store on the very next search, through the same projection the
// write seam uses. So the window where a search is lexically blind is closed before any query
// can observe it: the backfill runs at the head of HybridCandidatesAsync, ahead of the match.
//
// Nothing is LOST: search_fts is a derived mirror of plan_nodes/comments, never a source of
// truth (its Text is write-only — hits carry the entity address, and the row is re-projected,
// never read back). Deleting it costs one rebuild per project file, once.
//
// The typed API expresses this exactly (Delete.FromTable().AllRows() → `DELETE FROM search_fts`),
// so no SqliteDdl.Raw is needed even though the target is an FTS5 virtual table.
[Migration(15, "Empty search_fts so the lexical backfill re-projects nodes with slug terms")]
public sealed class M015_SlugInLexicalText : Migration
{
	public override void Up() => Delete.FromTable("search_fts").AllRows();

	// Forward-only. Down() cannot restore the pre-slug text and does not need to: the table is
	// derived, and an emptied index self-heals through the same backfill on the next search —
	// with whatever ToDoc the code at that moment says. Emptying it again would be theatre.
	public override void Down() { }
}
