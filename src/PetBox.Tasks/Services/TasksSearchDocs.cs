using PetBox.Core.Search;
using PetBox.Tasks.Data;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Services;

// Single source of truth for how a plan node maps onto the entity-addressed search contract.
// Tasks search indexes every node with a stable identity, terminal or not (search-hides-terminal-
// nodes) — membership is the IsIndexable predicate, and it no longer forks on terminality; a
// terminal-CANCEL node stays reachable via includeClosed, and terminal-OK (accepted/Done) is a
// success state search-before-rework must reach, so both need to be IN the index. Terminal
// visibility is now a QUERY-time filter (TasksService's query-mode candidate resolve), not an
// index-membership one. Entity address: Scope=projectKey, Type=Board (so a board filter is a
// SearchFilter(Type=board) and the per-board vector cursor uses IndexName=Board), Id=node slug
// (the temporal Key) — the temporal log's slugs map straight through, so renames/soft-deletes
// address the right row without needing a closed node's NodeId.
public static class TasksSearchDocs
{
	// The lexical projection's SCHEMA version (reindex-as-first-class-mechanism). TasksService's
	// EnsureLexicalBackfillAsync gates its rebuild on this number, stored per project file — not on
	// "search_fts has any row", which could never re-fire once a file had ANY row. Bump this
	// whenever ToDoc or CommentToDoc's projected TEXT/Key shape changes (the way search-slug-words-gap
	// once did), OR whenever IsIndexable's MEMBERSHIP changes (the way search-hides-terminal-nodes
	// does): every project file self-heals its search_fts on the next search, no migration needed for
	// the CONTENT. Bumped to 2 by search-key-column-everywhere: the slug moved out of `Text` into its
	// own indexed `Key` column (M015_SearchKeyColumn adds the column — a schema change no version
	// bump can express — this bump is what makes every existing row's Text/Key actually get
	// reprojected into the new shape). Bumped to 3 by search-hides-terminal-nodes: IsIndexable
	// stopped excluding terminal nodes, so every already-terminal node (previously deleted from the
	// index) needs to be (re)added — the LEXICAL half self-heals on the next search; the semantic
	// (vector) half only catches up incrementally as the async-vectorization worker drains forward,
	// so a project with old terminal nodes may need an explicit search_reindex to backfill their
	// vectors.
	//
	// Bumped to 4 by search-doc-model-title-weights: the node's title (Name) moved OUT of the spliced
	// `Text` blob into its own indexed `Title` column (M020_SearchTitleColumn adds the column — a
	// schema change no version bump can express; this bump is what reprojects every existing node's
	// Text/Title into the new two-column shape on the next search, so a deployed file's titles land in
	// the Title column without any node being re-saved).
	public const long LexicalProjectionVersion = 4;

	// The META projection's schema version (search-index-authority) — the reference-layer twin of
	// LexicalProjectionVersion, gating EnsureMetaBackfillAsync's rebuild of search_meta. Bump this
	// whenever ToMetaDoc's projected facets or alias set change shape, so every project file self-heals
	// its search_meta on the next search — same version-gated, no-migration mechanism the lexical floor
	// uses. Starts at 1: the birth of the reference layer, populated for the first time.
	public const long MetaProjectionVersion = 1;

	// Indexed iff the node has a stable identity — terminality no longer forks membership
	// (search-hides-terminal-nodes): a terminal node's VISIBILITY in a default query-mode result
	// is a read-time filter (hide terminal-CANCEL unless includeClosed), not an index-membership
	// one, so ranked search can still reach it when asked. The runtime parameter is kept for
	// call-site stability (many callers pass one) though it is no longer consulted here.
	public static bool IsIndexable(PlanNode n) => IsIndexable(n, MethodologyRuntime.PresetsOnly);

	public static bool IsIndexable(PlanNode n, MethodologyRuntime runtime)
	{
		_ = runtime;
		return n.NodeId.Length > 0;
	}

	// The slug is a lexicon term, in its OWN column (search-key-column-everywhere): the entity
	// address (Id) is `unindexed` in search_fts, so without this the slug is reachable only
	// through the exact retriever, and only when the WHOLE query is the slug verbatim. Slugs are
	// English kebab while titles/bodies are often Russian, so an English query built from the
	// slug's words (`methodology lifecycle ux`) falls in the gap: too fuzzy for exact, and its
	// terms are absent from the lexicon. Projecting the slug into `Key` turns it from
	// all-or-nothing into a per-word bridge between an English identifier and a Russian body —
	// SqliteFtsIndex's MATCH has no column filter, so a key-word query finds it same as before.
	//
	// search-slug-words-gap shipped this same bridge by SPLICING the slug onto the
	// front of `Text` (`n.Key + "\n" + n.Name + "\n" + n.Body`) — a poorer version of the same
	// idea per the owner: the key's words landed in Text's OWN term frequencies, double-counting
	// them into whatever BM25 score Text/Tags already carry. A dedicated column keeps the key's
	// contribution separate from prose instead.
	//
	// No manual split on `-` is needed and none is wanted: BOTH tokenizers already treat every
	// non-alphanumeric as a separator — fts5 `unicode61` on the index side (M009/M015) and
	// FtsQuery.Tokens (`[\p{L}\p{Nd}]+`) on the query side. So `methodology-lifecycle-ux` and
	// `methodology_lifecycle_ux` alike yield the terms methodology/lifecycle/ux, digits included
	// — the whole `[a-z][a-z0-9_-]*` slug grammar is covered by the shared tokenizer, and a
	// pre-split here would only produce the same terms one layer earlier (and could drift from it).
	// The slug is NOT re-matchable as one whole term — that address is the exact retriever's job,
	// which reads the temporal store, not this index.
	// Title (n.Name) and Body (n.Body) are now DECLARED as SEPARATE fields (search-doc-model-title-
	// weights), not spliced into one `Text` blob: the lexical leg weights a title hit above a body hit
	// (FtsColumnWeights), and the embed-template (SearchDoc.EmbedInput) recombines them as Name\nBody
	// — the exact string the old spliced Text carried, so the semantic vectors are unchanged.
	public static SearchDoc ToDoc(PlanNode n, string scope, IReadOnlyList<string> tags) =>
		new(scope, n.Board, n.Key, n.Body, string.Join(' ', tags), Key: n.Key, Title: n.Name);

	// Namespace prefix for a comment's FTS Id. A node slug is `[a-z][a-z0-9_-]*` (no colon
	// ever), so "c:" + commentKey is collision-free within the same Type=board partition — a
	// comment doc keeps Type=board (board scoping) but is addressed apart from node slugs.
	public const string CommentIdPrefix = "c:";

	// A comment as a board-search doc: Type=board (owner node's board) keeps board scoping,
	// Id="c:"+Key namespaces it off node slugs, Text=Body. Tags are set post-upsert (SetTagsAsync
	// runs after the temporal write), so they aren't cheaply available on the write path — left
	// null; the read path resolves the hit to its OWNER node regardless. Key is left at its
	// default ("") ON PURPOSE (search-key-column-everywhere): a comment's OWN key (CommentRow.Key)
	// is a random GUID, not an English/Russian lexicon word a caller would type — populating it
	// would only add noise tokens, not a search bridge like a node's slug is. Title is likewise left
	// "" (search-doc-model-title-weights): a comment is a titleless doc-type — it has a body and no
	// title, so its whole prose is Body, weighted as body. EmbedInput then collapses to that body
	// alone, exactly as it embedded before the Title field existed.
	public static SearchDoc CommentToDoc(CommentRow c, string scope) =>
		new(scope, c.Board, CommentIdPrefix + c.Key, c.Body, null);

	// The node's row in the META reference layer (search-index-authority): the entity address
	// (Scope=scope, Type=Board, Id=Key — the SAME address as ToDoc, so a node's text row and facet row
	// line up), the computed facets, and the identity alias set. Written in the same entity transaction
	// as ToDoc so a committed node's membership and facets never lag its text.
	//
	// StatusKind is taken from the SINGLE authority — MethodologyRuntime.StatusKindOf(kindSlug, status)
	// — never recomputed here; `kindSlug` is the node's board kind on the write path (per-board
	// classification) and null on the file-wide backfill (project-wide classification, StatusKindOf's
	// KindOfSlug fallback). Aliases are the node's slug AND its NodeId: the slug doubles the Id on
	// purpose (an identity lookup then resolves ANY node through the one alias table, Id included), and
	// the NodeId is the identifier the lexical index does NOT carry — closing exactly the "agents search
	// by node id and get nothing" gap this reference layer exists to close.
	public static SearchMetaDoc ToMetaDoc(PlanNode n, string scope, MethodologyRuntime runtime, string? kindSlug) =>
		new(scope, n.Board, n.Key,
			StatusKind: StatusKindFacet(runtime.StatusKindOf(kindSlug, n.Status)),
			Created: n.Created,
			Updated: n.Updated,
			Aliases: [n.Key, n.NodeId]);

	// The StatusKind facet string — the enum name lowercased (open|terminalok|terminalcancel), the
	// SAME vocabulary the methodology contract exposes (MethodologyReference derives it identically from
	// Enum.GetNames<StatusKind>()). A status the authority cannot classify (an out-of-vocab legacy slug
	// → null) is, by the membership rule, a non-terminal member of the index, so it reads as "open".
	static string StatusKindFacet(StatusKind? kind) => (kind ?? StatusKind.Open).ToString().ToLowerInvariant();

	// The StatusKind facet VALUE a default query-mode search hides (search-hides-terminal-nodes):
	// terminal-CANCEL. This is the tasks-specific value the general facet-pushdown mechanism (spec
	// search-facet-pushdown, SearchFilter.Facets.ExcludeStatusKinds) excludes in each leg BEFORE
	// truncation unless includeClosed widened the ask. Derived from the SAME StatusKindFacet
	// projection the write path stamps into search_meta, so the pushdown predicate and the stored
	// facet can never drift to different spellings — never a bare "terminalcancel" literal.
	public static readonly string TerminalCancelFacet = StatusKindFacet(StatusKind.TerminalCancel);
}
