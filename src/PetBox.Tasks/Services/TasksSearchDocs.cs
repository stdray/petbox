using PetBox.Core.Search;
using PetBox.Tasks.Data;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Services;

// Single source of truth for how a plan node maps onto the entity-addressed search contract.
// Tasks search covers only the OPEN set (active, non-terminal) — terminal/closed nodes are dropped
// from the index, so membership is the IsIndexable predicate. Entity address: Scope=projectKey,
// Type=Board (so a board filter is a SearchFilter(Type=board) and the per-board vector cursor uses
// IndexName=Board), Id=node slug (the temporal Key) — the temporal log's slugs map straight through,
// so renames/soft-deletes address the right row without needing a closed node's NodeId.
public static class TasksSearchDocs
{
	// Indexed iff the node has a stable identity and is not in a terminal workflow state.
	// The runtime overload also recognizes a project definition's terminal statuses; the
	// bare form is the presets-only view (background board walkers without a runtime).
	public static bool IsIndexable(PlanNode n) => IsIndexable(n, MethodologyRuntime.PresetsOnly);

	public static bool IsIndexable(PlanNode n, MethodologyRuntime runtime) =>
		n.NodeId.Length > 0 && !runtime.IsTerminalSlug(n.Status);

	// The slug LEADS the indexed text (search-slug-words-gap): the entity address (Id) is
	// `unindexed` in search_fts, so before this the slug was reachable ONLY through the exact
	// retriever, and only when the WHOLE query was the slug verbatim. Slugs are English kebab
	// while titles/bodies are often Russian, so an English query built from the slug's words
	// (`methodology lifecycle ux`) fell in the gap: too fuzzy for exact, and its terms were
	// absent from the lexicon. Feeding the slug to the lexical floor turns it from all-or-nothing
	// into a per-word bridge between an English identifier and a Russian body.
	//
	// No manual split on `-` is needed and none is wanted: BOTH tokenizers already treat every
	// non-alphanumeric as a separator — fts5 `unicode61` on the index side (M009) and
	// FtsQuery.Tokens (`[\p{L}\p{Nd}]+`) on the query side. So `methodology-lifecycle-ux` and
	// `methodology_lifecycle_ux` alike yield the terms methodology/lifecycle/ux, digits included
	// — the whole `[a-z][a-z0-9_-]*` slug grammar is covered by the shared tokenizer, and a
	// pre-split here would only produce the same terms one layer earlier (and could drift from it).
	// The slug is NOT re-matchable as one whole term — that address is the exact retriever's job,
	// which reads the temporal store, not this index.
	public static SearchDoc ToDoc(PlanNode n, string scope, IReadOnlyList<string> tags) =>
		new(scope, n.Board, n.Key, n.Key + "\n" + n.Name + "\n" + n.Body, string.Join(' ', tags));

	// Namespace prefix for a comment's FTS Id. A node slug is `[a-z][a-z0-9_-]*` (no colon
	// ever), so "c:" + commentKey is collision-free within the same Type=board partition — a
	// comment doc keeps Type=board (board scoping) but is addressed apart from node slugs.
	public const string CommentIdPrefix = "c:";

	// A comment as a board-search doc: Type=board (owner node's board) keeps board scoping,
	// Id="c:"+Key namespaces it off node slugs, Text=Body. Tags are set post-upsert (SetTagsAsync
	// runs after the temporal write), so they aren't cheaply available on the write path — left
	// null; the read path resolves the hit to its OWNER node regardless.
	public static SearchDoc CommentToDoc(CommentRow c, string scope) =>
		new(scope, c.Board, CommentIdPrefix + c.Key, c.Body, null);
}
