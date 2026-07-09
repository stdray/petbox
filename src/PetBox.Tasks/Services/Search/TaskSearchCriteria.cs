namespace PetBox.Tasks.Services.Search;

// Resolved post-select predicates for the unified tasks read (SearchNodesAsync). Every field
// is a SOFT filter: null = no filter; an empty set means "provided but nothing matched" and
// yields an empty result (distinct from "not given"). Built once per request after soft
// identifier resolution (keys/under) and status/commit lookup; applied by TaskSearchFilter.
public sealed record TaskSearchCriteria(
	// part_of subtree roots (NodeIds). A node passes if it is in ANY root's subtree.
	// ParentOf is required when UnderRoots is non-null (even empty — empty already matches nothing).
	IReadOnlySet<string>? UnderRoots = null,
	IReadOnlyDictionary<string, string>? ParentOf = null,
	// Status slugs (case-insensitive set). Naming a terminal slug is an explicit ask.
	IReadOnlySet<string>? StatusSlugs = null,
	// Addressed nodes by stable NodeId (slug|NodeId mixed resolution upstream).
	IReadOnlySet<string>? KeyNodeIds = null,
	// Reverse commit lookup: NodeIds carrying the requested commit (exact or >=7-hex prefix).
	IReadOnlySet<string>? CommitNodeIds = null);
