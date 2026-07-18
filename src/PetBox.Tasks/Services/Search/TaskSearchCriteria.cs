namespace PetBox.Tasks.Services.Search;

// Resolved post-select predicates for the unified tasks read (SearchNodesAsync). Every field
// is a SOFT filter: null = no filter; an empty set means "provided but nothing matched" and
// yields an empty result (distinct from "not given"). Built once per request after soft
// identifier resolution (keys/under) and status/commit lookup; applied by TaskSearchFilter.
//
// These are ENTITY predicates (spec tasks-search-entity-predicates-under-commit): the опорный слой
// (search_meta) holds the StatusKind facet + identity aliases, NOT the part_of graph or commit
// trailers — so `UnderRoots` (a tasks-only part_of subtree) and `CommitNodeIds` (a tasks-only
// attribute) are resolved on the entity side and applied at the pipeline's re-filter step. They
// NARROW the already-selected pool; they never grant selecting past the опорный слой.
public sealed record TaskSearchCriteria(
	// Entity predicate: part_of subtree roots (NodeIds) — the tasks-only graph the опорный слой
	// cannot express. A node passes if it is in ANY root's subtree. ParentOf is required when
	// UnderRoots is non-null (even empty — empty already matches nothing).
	IReadOnlySet<string>? UnderRoots = null,
	IReadOnlyDictionary<string, string>? ParentOf = null,
	// Status slugs (case-insensitive set). Naming a terminal slug is an explicit ask.
	IReadOnlySet<string>? StatusSlugs = null,
	// Addressed nodes by stable NodeId (slug|NodeId mixed resolution upstream).
	IReadOnlySet<string>? KeyNodeIds = null,
	// Entity predicate: reverse commit lookup — NodeIds carrying the requested commit (exact or
	// >=7-hex prefix). A tasks-only attribute the опорный слой does not index.
	IReadOnlySet<string>? CommitNodeIds = null);
