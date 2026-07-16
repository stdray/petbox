namespace PetBox.Tasks.Workflow;

// A pure projection of PetBox.Tasks.Data.PlanNode carrying only the fields the guard/
// resolve decisions actually read (methodology-engine-extraction, slice 2, condition 4):
// RequireDefinitionLinks, RequireBlockersAsync, RequirePreconditionArtifactsAsync,
// ResolveBlockedBy (TasksService.cs) key/branch on exactly these five. PlanNode itself
// can't be referenced from this assembly — Data/PlanNode.cs:1 `using LinqToDB.Mapping`
// would drag linq2db into the DB-free engine — so the service maps PlanNode -> NodeState
// at the IO boundary instead of fluent-mapping PlanNode into a clean type.
//
// Deliberately NOT carried: Board (guards take the board as a separate parameter),
// Name/Body/Priority/Commits (never read by a guard), Version/ActiveFrom/ActiveTo/Created/
// Updated (temporal-store bookkeeping, not FSM decision input). Add a field only when a
// slice-3 solver is shown to read it off PlanNode — don't pre-guess the shape.
public sealed record NodeState(
	string Key,
	string? PrevKey,
	string NodeId,
	string Status,
	string Type);
