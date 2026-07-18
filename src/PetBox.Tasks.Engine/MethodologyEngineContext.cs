namespace PetBox.Tasks.Workflow;

// The IO-free INPUT of the methodology engine (methodology-engine-extraction, slice 3): every
// row the guards and the resolvers used to fetch for themselves, prefetched ONCE by the service
// and handed over as data. Nothing here is a decision — these are RAW candidates (condition 3):
// the engine resolves slugs against them, it is never given a resolved NodeId.
//
// STABLE ACROSS THE RETRY LOOP, on purpose. `UpsertAsync`'s partial-mode loop re-runs the engine
// after every rejection with a narrowed `live` set (condition 5); the per-pass inputs (desired /
// prior states, the raw link fields) are therefore ARGUMENTS to GuardEngine.Decide, not fields
// here. The context is what the service paid IO for; it is built once and never rebuilt.
//
// Deviation from the sketch in doc/methodology-redesign/04-engine-extraction.md: the sketch put
// `desiredStates`/`priorStates` in the context. They change every pass, so keeping them here
// would make "prefetched once" a lie and invite a per-pass refetch.
public sealed record MethodologyEngineContext(
	MethodologyRuntime Runtime,
	string? KindSlug,
	// The board as the CALLER addressed it (the `board` argument of the upsert) — the blockedBy
	// resolution names it in its error. `BoardName` is the board meta's own name, which the
	// ideaRef error names. They are normally the same string; carried apart so neither message
	// silently changes shape if a lookup ever normalizes case.
	string Board,
	string BoardName,
	string? SpecBoard,
	string MethodologyInstance,
	// nodeId -> the node it addresses, across EVERY board of the project (links bind to NodeId,
	// which is globally unique). Empty when this batch carries no specRef/ideaRef at all — the
	// service does not pay for the scan when no resolution or link-target check can run.
	IReadOnlyDictionary<string, NodeIndexEntry> NodeIndex,
	// The project's boards (raw candidates for the ideaRef instance-bucket selection).
	IReadOnlyList<EngineBoard> Boards,
	// nodeId -> the NodeIds of the ACTIVE `blocks` edges pointing INTO it (condition 2). A node
	// absent from the map has none — a node born in this call cannot have an inbound edge yet.
	IReadOnlyDictionary<string, IReadOnlyList<string>> BlockerEdgesByNodeId,
	// nodeId -> the NodeIds of its `part_of` children whose OWN row is still active (condition 2).
	// The delete guard's input; it lives outside the retry loop in the service and refuses with a
	// conflict shape rather than an exception, so it reads this map directly.
	IReadOnlyDictionary<string, IReadOnlyList<string>> PartOfChildrenByNodeId,
	// nodeId -> the flat tag set of its active comments (the precondition-artifact gate's input).
	// The engine declares the need via GuardEngine.NeedsCommentTags; the service prefetches the
	// whole board in one pass instead of the old per-node read in the middle of the decision.
	IReadOnlyDictionary<string, IReadOnlyList<string>> CommentTagsByNodeId);

// One entry of the project-wide node index — the engine's read-only view of a link target.
// Title is deliberately absent: no guard reads it (the view layer's NodeRef still carries it).
public sealed record NodeIndexEntry(string NodeId, string Board, string BoardKind, string Slug, string Status, string Type);

// A board as the engine sees it: enough to pick the ideaRef target boards of this methodology
// instance, nothing more.
public sealed record EngineBoard(string Name, string Kind, string MethodologyInstance, bool Closed);

// What kind of refusal a verdict is. The exception TYPE is load-bearing at the service boundary
// (NodeRejection.cs: callers and xUnit's Assert.Throws<T> match it exactly), so the pure engine
// names it rather than inventing one when the service re-raises.
public enum VerdictKind
{
	// -> ArgumentException: the payload is malformed / does not resolve.
	InvalidArgument,
	// -> InvalidOperationException: the process refused a well-formed request.
	InvalidOperation,
}

// A refusal that indicts ONE node: exactly the (type, message, node key) triple the guards used
// to throw inline. `Node` is what the service tags onto the exception via ForNode — the key the
// partial-mode retry loop retires from the batch.
public sealed record MethodologyVerdict(string Node, string Message, VerdictKind Kind);

// One RESOLVED link edge the write door produces (spec methodology-link-kinds-declared): the
// writer node (`WriterKey`, the upserted node of the board's kind) links `TargetNodeId` via
// relation kind `Kind`. `WriterIsFrom` fixes the stored edge's orientation — true = the writer is
// relations.from (work→spec task_spec), false = the writer is relations.to (idea→spec idea_spec,
// blocker→task blocks). The generic resolver derives this from the link kind's Direction (which
// end's kind equals the writer's kind); a direction-less builtin (`blocks`) fixes it to false.
public sealed record ResolvedLink(string Kind, string WriterKey, string TargetNodeId, bool WriterIsFrom);

// The engine's OUTPUT: pre-write verdicts ONLY (condition 1). There is no `effectsToApply` and
// there never can be — the post-write effects run over `landed`, which is decided by
// TemporalStore's conflicts and is unknowable before the write.
//
// `Verdicts` is FAIL-FAST: the engine stops at the first refusal, exactly where the old code
// threw, so it carries at most one entry today. That is not a limitation to relax casually —
// running on past a refusal would mean judging with a half-resolved ref map and manufacturing
// verdicts that never existed (a failed link resolution would make RequireDefinitionLinks
// "discover" a missing link). The list shape is what lets a later slice widen this deliberately.
//
// `Links` is pre-write DATA, not effects: the resolved link edges (condition 3) the post-write
// edge writes (LinkRefsAsync) consume. Only meaningful when `Verdicts` is empty.
public sealed record MethodologyEngineDecision(
	IReadOnlyList<MethodologyVerdict> Verdicts,
	IReadOnlyList<ResolvedLink> Links)
{
	public static MethodologyEngineDecision Refused(MethodologyVerdict v) =>
		new([v], []);
}
