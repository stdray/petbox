using FluentMigrator;

namespace PetBox.Core.Data.Migrations;

// Move the sandbox project `smoke` out of the `$system` workspace and into its own (work
// `smoke-own-workspace-containment`). This is DEFENCE IN DEPTH, not a bug fix — the bug was fixed in
// `d322fd8d` (SandboxContainment now guards the derived hop at all three sites). This changes where
// the sandbox SITS, so that the next derivation bug lands on nothing.
//
// WHY THE TOPOLOGY WAS THE REAL EXPOSURE. A shared-memory container is DERIVED from a project's
// workspace (WorkspaceMemory.ContainerKeyFor): `$system` -> `$workspace`, anything else ->
// `$ws-<key>`. `smoke` sat in `$system`, so the container derivable FROM THE SANDBOX was
// `$workspace` — the owner's cross-project notes and the canon index. That is why a containment hole
// shipped as a real disclosure: `GET /api/memory/smoke/canon` with the sandboxOnly key returned 1309
// chars of `$system` workspace canon. After this migration the same derivation yields `$ws-smoke`,
// which holds nothing, so the equivalent future bug discloses an empty container instead of the
// owner's memory. The defect class has recurred roughly monthly; this removes the asset from behind
// the door rather than betting on the lock.
//
// THE CONTAINER ROW IS DELIBERATELY NOT CREATED HERE. Container rows are LAZY —
// WorkspaceMemory.EnsureContainerAsync inserts one on first resolve, and the live installation
// already demonstrates the state: workspace `uiaudit-tmp` has a project and NO `$ws-uiaudit-tmp` row.
// Creating `$ws-smoke` eagerly would only manufacture the very thing this migration exists to leave
// empty. Nothing needs it to exist: a sandboxOnly key is refused at a container regardless, because
// a container row is never Sandbox=true (SandboxContainment's predicate).
//
// THE LLM REGISTRY KEEPS WORKING, and this is the one dependency worth stating. The registry level is
// derived from the workspace too (LlmRegistryEditor): workspace `$system` IS level `System:$`, any
// other workspace is `Workspace:<key>`. A workspace that declares no registry INHERITS the system one
// — both switches in LlmRegistryInheritanceSettings default TRUE — so `smoke` keeps resolving the same
// endpoints for Embed/Chat and background jobs (vectorization, digests) go on working there, which
// AGENTS.md rule 7 requires of a smoke target. The move also closes a second instance of the same
// topology bug in passing: while `smoke` was in `$system`, an `llm_config_upsert` aimed at it wrote
// the LIVE `System:$` registry every other workspace inherits.
//
// GUARD. Project keys are globally unique, so `Key = 'smoke'` identifies the row on its own; the
// `WorkspaceKey = '$system'` clause makes the statement idempotent (a second run matches nothing) and
// makes it a no-op on every fresh database, where no `smoke` project exists at all. The Sandbox flag
// is deliberately NOT part of the predicate: a guard that silently matches nothing is
// indistinguishable from a guard that ran, and this statement must never be a silent no-op on prod.
[Migration(48, "Move the sandbox project 'smoke' into its own workspace (empty derivable container)")]
public sealed class M048_SmokeOwnWorkspace : Migration
{
	const string SmokeWorkspace = "smoke";

	public override void Up()
	{
		// The workspace must exist before the project can point at it (ProjectDirectory.CreateAsync
		// refuses an orphan row for the same reason). INSERT OR IGNORE keeps a re-run harmless.
		//
		// CONDITIONAL ON THE PROJECT BEING HERE, and that is not a detail. This migration runs on every
		// database that ever exists — every fresh install, every test fixture — and only ONE of them has
		// a `smoke` project to relocate. An unconditional INSERT manufactured an empty `smoke` workspace
		// on all of them; the suite caught it immediately (WorkspaceAdminServiceTests.List_returns_every
		// _workspace_ordered_by_key and NavigationContextTests.Sysadmin_sees_every_workspace_regardless
		// _of_membership both enumerate the seeded workspaces and both went red). Those tests were
		// right: a data migration for one installation's row must be inert everywhere that row is
		// absent, or it stops being a migration and becomes a seed.
		Execute.Sql(
			"INSERT OR IGNORE INTO Workspaces (Key, Name, Description, CreatedAt) " +
			$"SELECT '{SmokeWorkspace}', 'Smoke', " +
			"'Live-smoke sandbox. Isolated so the container derivable from `smoke` holds nothing.', " +
			"datetime('now') " +
			"WHERE EXISTS (SELECT 1 FROM Projects WHERE Key = 'smoke' AND WorkspaceKey = '$system')");

		Execute.Sql(
			$"UPDATE Projects SET WorkspaceKey = '{SmokeWorkspace}' " +
			"WHERE Key = 'smoke' AND WorkspaceKey = '$system'");
	}

	// REVERSIBLE ON PURPOSE — this is the rollback path, and it is why the move is expressed as a
	// migration rather than a hand-run UPDATE. It puts the project back and leaves the (empty)
	// workspace row: dropping the workspace could orphan a `$ws-smoke` container if one had since been
	// lazily created, and an unused Workspaces row costs nothing.
	public override void Down() =>
		Execute.Sql(
			"UPDATE Projects SET WorkspaceKey = '$system' " +
			$"WHERE Key = 'smoke' AND WorkspaceKey = '{SmokeWorkspace}'");
}
