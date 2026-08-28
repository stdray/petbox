namespace PetBox.Tasks.Contract;

// The task-board twin of PetBox.Memory.Contract.MemoryStores.SystemStoreNames (work
// observation-kind-and-dedup): board NAMES that are code-declared plumbing, not user data —
// currently just `observations` (kind `observation`, board-create-and-dedup). Unlike a
// memory system store (auto-vivified lazily on first write, IsSystem tagged after the
// fact), a system BOARD is provisioned eagerly — see PetBox.Web.Tasks.ObservationsBoardSeeder
// — because a board must exist before tasks_search/tasks_node_get can show it, whereas a
// store's catalog row genuinely doesn't matter until something writes to it.
//
// IsSystem gates ONLY the delete/close guard (TasksService.DeleteBoardAsync/SetClosedAsync)
// — same narrow scope as MemoryStore.IsSystem: it must never block ordinary node writes on
// the board (tasks_upsert keeps working), only the two irreversible/freezing board-level
// acts. No such guard existed anywhere in the codebase before this (memory's own IsSystem
// delete-guard turned out to live ONLY in the admin Razor page, not the service door — see
// ProjectMemory.cshtml.cs) — this is deliberately the minimal version, at the one door
// (the service layer) that protects every caller (MCP tool, admin UI, any future one) at
// once rather than repeating the check per caller.
public static class SystemBoards
{
	public const string Observations = "observations";
	public const string ObservationKind = "observation";

	public static readonly IReadOnlySet<string> Names =
		new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Observations };

	public static bool IsSystem(string? board) =>
		!string.IsNullOrWhiteSpace(board) && Names.Contains(board.Trim());
}
