namespace PetBox.Tasks.Services.NodeRef;

// The four identity-resolution policies formerly inlined as separate methods on TasksService.
// Return shapes differ, so NodeRefResolver exposes one method per policy rather than a single
// switch — this enum is the shared vocabulary for that mapping.
public enum NodeRefPolicy
{
	// ResolveNodeRefAsync: miss + ambiguity throw; NodeId passthrough.
	Strict,
	// ResolveNodeRefOrNullAsync: miss → null; ambiguity still throws; NodeId passthrough.
	SoftNull,
	// ExactIdentifierHitsAsync: soft multi-hit (miss → empty, ambiguity → all matches).
	MultiHit,
	// ResolveSlugsAsync: batch + PrevKey rename lineage; omit miss/ambiguity.
	BatchRename,
}
