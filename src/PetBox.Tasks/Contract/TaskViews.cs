using PetBox.Core.Data.Temporal;
using PetBox.Tasks.Data;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Contract;

// Public response shapes for the Tasks service. Adapters serialize these (the MCP
// serializer camelCases them); the service owns how they are computed.

// Resolves a link target to its location; a `Status` of "missing" means the target
// no longer exists.
public sealed record LinkDto(string NodeId, string? Board, string? L1, string? L2, string? L3, string? Title, string Status);

// A plan node enriched with its links and (on a spec board) computed delivery roll-up.
public sealed record PlanNodeView(
	string Key, string NodeId, string L1, string? L2, string? L3, int Depth, string? ParentKey,
	string Status, string Type, string Title, string Body, string? CommitRef, long Priority, long Version,
	string? Delivery, IReadOnlyList<LinkDto>? Spec, IReadOnlyList<LinkDto>? BlockedBy,
	IReadOnlyList<LinkDto>? LinkedTasks, IReadOnlyList<string> RenamedFrom);

// A board's active plan nodes as a 1-to-3 level tree, plus the board's kind and
// (work boards) the spec board its tasks link into.
public sealed record PlanBoardView(long CurrentVersion, string Kind, string? SpecBoard, IReadOnlyList<PlanNodeView> Nodes);

// Delta projection of a node (no links/delivery — that's GetAsync).
public sealed record PlanNodeDelta(
	string Key, string NodeId, string L1, string? L2, string? L3, int Depth, string? ParentKey,
	string Status, string Type, string Title, string Body, string? CommitRef, long Priority, long Version);

// The raw temporal upsert/delta result plus the board kind, ready for an adapter to
// serialize. The service owns the logic; the adapter owns the wire shape.
public sealed record UpsertOutcome(TemporalUpsertResult<PlanNode> Result, BoardKind Kind);
