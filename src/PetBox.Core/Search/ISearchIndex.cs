using LinqToDB.Data;

namespace PetBox.Core.Search;

// The consistency class of an index's write path — the load-bearing distinction of the
// search contract (design: memory m-b3fbe908).
//   Synchronous = the lexical floor: updated INSIDE the entity's DB transaction (same
//     SQLite file/connection), commits/rolls back WITH the entity → a committed entity is
//     never lexically-stale. MUST be local (transactional ⊥ remote; no 2PC).
//   Eventual    = vector / episodic enrichment: materialized later by a background worker
//     that subscribes to the temporal log via its own cursor (async-vectorization). May be
//     remote/pluggable; best-effort; search degrades gracefully when it is behind or absent.
public enum SearchConsistency
{
	Synchronous,
	Eventual,
}

// What an index can answer. Flags so one index could declare several; the facade lifts
// these to read-path provenance (which retrievers actually ran).
[Flags]
public enum SearchCapability
{
	None = 0,
	Lexical = 1,
	Vector = 2,
}

// The MEMBERSHIP class of a leg — the load-bearing distinction the pipeline contract makes
// explicit (spec: search-leg-classification). It is NOT about which retriever ran (that is
// Capability/provenance); it is about whether the leg has a notion of "ALL that matched":
//   Enumerable = identity + lexical. Can return its ENTIRE matched set — a boolean membership
//     predicate (this entity matched, that one did not). A field/scan selection that needs the
//     FULL set can therefore be answered by an enumerable leg alone.
//   TopK       = the vector leg. Cosine ranks EVERY candidate; there is no "matched / did not
//     match", only the K nearest. It has NO boolean membership, so it can never supply "all that
//     matched". A cosine >= tau threshold would FORGE one — REJECTED (that is the SemanticFloor
//     through the back door); the vector leg participates only in RELEVANCE selection, as a peer.
public enum SearchLegClass
{
	Enumerable,
	TopK,
}

// The SELECTION axis of a read (spec: search-selection-vs-presentation) — WHAT enters the output,
// kept separate from the PRESENTATION axis (the order shown, decided by the consumer's sort). The
// two are split precisely because mixing them silently drops results.
//   Relevance  = the fused top-K ask: EVERY leg selects and vector-only candidates ENTER as peers
//     (they are not merely reordering what lexical found). The facade fuses all legs by RRF and
//     truncates to k.
//   Enumerable = the scan/field ask: it needs the FULL matched set, which only enumerable legs can
//     supply, so the TopK (vector) leg is categorically excluded — a VISIBLE contract limit carried
//     as `semantic:false` in provenance, never a silent omission. No truncation: the whole set is
//     returned and the consumer presents/limits it.
public enum SearchSelection
{
	Relevance,
	Enumerable,
}

// A document to index, addressed by ENTITY (scope, type, id) — never by row. Resolving the
// entity back from (type, id) is the consumer's job; the contract only carries the searchable
// text + optional free tags. (spec: search-entity-addressed.)
//
// This is the DECLARED document model (spec: search-doc-model): each field is an explicit facet of
// the entity's lexicon, NOT an accident of which prose happened to be concatenated into one blob.
// `Title` is the entity's title in its OWN column — n.Name for a task node, e.Description for a
// memory entry (Description IS a memory's title, a free port). `Text` is the BODY alone. Keeping
// them apart is what lets the lexical leg weight a title hit above a body hit (search-doc-model-
// title-weights) — a splice into one `Text` blob makes that impossible. Every family declares its
// Title; a doc-type with no natural title (a task comment) simply leaves it "".
//
// `Key` (search-key-column-everywhere) is the entity's own business key/slug — n.Key for a task
// node, e.Key for a memory entry — projected into its OWN indexed column instead of being spliced
// into `Text`. Slugs are English kebab while titles/bodies are often Russian; a dedicated column
// keeps the key's tokens searchable WITHOUT mixing them into the prose term frequencies (a splice
// there double-counts the key's words and skews BM25 — see TasksSearchDocs' history). Distinct
// from `Id`: `Id` is the (unindexed) row ADDRESS an index resolves a hit back to, which for a
// comment is a namespaced "c:"+guid, not a word a caller would type. `Key` is OPTIONAL (default
// "") — an entity with no meaningful lexicon key (e.g. a comment, addressed by a random GUID)
// simply leaves it empty; an index with no dedicated Key column (none yet outside SqliteFtsIndex)
// ignores it. `Title` is OPTIONAL the same way.
public readonly record struct SearchDoc(string Scope, string Type, string Id, string Text, string? Tags = null, string Key = "", string Title = "")
{
	// The EMBED-TEMPLATE, DECLARED (spec: search-doc-model): "what represents this entity's MEANING"
	// for the vector (Class-B) leg = Title + Body. This is a first-class mapping property, NOT a side
	// effect of which column an index happens to call `Text`. The lexical leg weights the fields
	// apart (Title vs Body columns); the semantic leg embeds them AS ONE meaning-bearing string, so
	// the declaration lives here on the doc rather than being re-derived at each embed call site.
	// Title-then-Body, newline-joined; an empty Title collapses to just the Body (a titleless comment
	// embeds its body alone, exactly as before this field existed).
	public string EmbedInput => Title.Length == 0 ? Text : Title + "\n" + Text;
}

// A single match: the entity identity (type, id), a per-index relevance score (scales differ
// across indexes — the facade fuses by RANK, not raw score), and which retriever produced it.
public readonly record struct Hit(string Type, string Id, double Score, string? Retriever = null);

// A FACET predicate pushed INTO each leg's candidate query, applied BEFORE the leg truncates at
// k (spec search-facet-pushdown) — joined against the search_meta reference layer by the entity
// address (Scope, Type, Id), the SAME address the text/vector rows carry. The pushdown is what
// lets the caller drop a compensating over-fetch pool: a candidate a facet excludes never occupies
// a top-k slot, so it never has to be re-fetched around.
//
// `ExcludeStatusKinds` drops entities whose search_meta.StatusKind is in the set — the general
// mechanism; the tasks-specific VALUE (hide terminal-cancel unless includeClosed) is chosen by the
// caller. An entity with NO search_meta row (e.g. a tasks comment doc, which carries text but no
// facet row) is KEPT — a facet it does not carry cannot hide it, matching the pre-pushdown behavior
// where such a hit resolved through its owner. Null/empty set = neutral (no facet narrowing).
public readonly record struct FacetFilter(IReadOnlyList<string>? ExcludeStatusKinds = null);

// Read-path narrowing. `Type` pins ONE entity type; `Types` is an include-SET over the same
// column — the seam that lets a consumer whose containers share one file (memory stores in
// memory/{project}.db, Type = store name) narrow a SINGLE index query to several containers at
// once, instead of running one query per container and merging by hand. Null/empty = no
// narrowing on that axis; when both are set, both must hold. `Facets` is the OPTIONAL facet
// pushdown (search-facet-pushdown): null — the default, and what a file with no search_meta table
// (memory today) always passes — emits no join, so it is a no-op there.
public readonly record struct SearchFilter(string? Type = null, IReadOnlyList<string>? Types = null, FacetFilter? Facets = null);

// A fused search response: the ranked hits plus honest provenance (which retrievers ran and
// whether the result is degraded). (spec: search-provenance.)
public readonly record struct SearchResponse(IReadOnlyList<Hit> Hits, SearchRetrievers Retrievers);

// One pluggable index behind the search contract. The facade (SearchService) routes writes by
// ConsistencyClass and fuses reads across every registered index.
//
// Writes take the caller's open transaction (`tx`): a Synchronous index MUST use it so its
// update joins the entity's commit/rollback. An Eventual index ignores `tx` (it is driven by a
// background cursor, not the write path) — the facade passes null for it.
public interface ISearchIndex
{
	SearchConsistency ConsistencyClass { get; }
	SearchCapability Capability { get; }

	// The leg's membership class (spec: search-leg-classification). Defaulted off Capability so
	// every existing/test index classifies correctly without a change: a Vector-capable leg is
	// TopK (cosine has no boolean membership), everything else (identity, lexical) is Enumerable.
	// An index may override to state its class outright.
	SearchLegClass LegClass => Capability.HasFlag(SearchCapability.Vector) ? SearchLegClass.TopK : SearchLegClass.Enumerable;

	Task IndexAsync(DataConnection? tx, SearchDoc doc, CancellationToken ct = default);
	Task DeleteAsync(DataConnection? tx, string scope, string type, string id, CancellationToken ct = default);
	// Board/type-wide purge: remove EVERY doc under (scope, type) in one shot. Used when a whole
	// container (e.g. a task board) is dropped and its per-id docs would otherwise be orphaned.
	Task DeleteByTypeAsync(DataConnection? tx, string scope, string type, CancellationToken ct = default);
	Task<IReadOnlyList<Hit>> SearchAsync(string scope, string query, SearchFilter filter, int k, CancellationToken ct = default);
}
