using PetBox.Core.Models;
using PetBox.Tasks.Contract;

namespace PetBox.Web.Tasks;

// The task-board twin of ProjectCanonSeeder/ProjectAgentDefSeeder (work
// observation-kind-and-dedup, owner clarification: `observations` is a system builtin board,
// like memory's builtin stores, not a document a human sets up once via
// tasks_methodology_utility_upsert). ITS OWN FILE for the same reason those two are: a fresh
// project gets the board without a manual step, and this is the ONE place that provisioning
// logic lives — ProjectDirectory.CreateAsync (the sole service-layer project writer) hangs a
// SeedObservationsBoardAsync off it, and Program.cs's startup migration pass (mirroring
// WorkDeferredStatusMigrator's "reconcile every existing project" shape, but with nothing to
// migrate — just ensure) calls this SAME seeder for every project that predates this card, so
// an already-live project (starting with $system) gets the board too, not just future ones.
//
// Board world: TaskBoardMeta.UtilityWorld ("$utility") — deliberately OUTSIDE any methodology
// instance, so CreateBoardAsync never hits the "methodology instance required" branch
// regardless of whether the project has quartet boards provisioned yet, AND so the board never
// enters MethodologyRuntime.PipelineOrder/EffectiveKinds() (kept out of the process guide and
// the owner-decision queue by construction — spec observation-stays-out-of-the-owner-queue).
public interface IObservationsBoardSeeder
{
	Task SeedAsync(string projectKey, CancellationToken ct = default);
}

public sealed class ObservationsBoardSeeder(ITasksService tasks, ILogger<ObservationsBoardSeeder>? log = null)
	: IObservationsBoardSeeder
{
	// Best-effort, NEVER throws — same contract as ProjectCanonSeeder/ProjectAgentDefSeeder: a
	// storage hiccup here must not turn into a refused project creation (or a failed startup
	// pass touching every OTHER project after this one).
	//
	// Idempotent by an explicit probe (not by racing CreateBoardAsync's "already exists"
	// exception): BoardExistsAsync first, skip if true. A concurrent double-create still lands
	// safely — CreateBoardAsync's own uniqueness check turns the loser into a caught exception
	// here, logged and swallowed, same as any other seed failure.
	public async Task SeedAsync(string projectKey, CancellationToken ct = default)
	{
		try
		{
			if (await tasks.BoardExistsAsync(projectKey, SystemBoards.Observations, ct))
				return;
			await tasks.CreateBoardAsync(
				projectKey, SystemBoards.Observations, SystemBoards.ObservationKind,
				description: "System board: captured observations (dedup'd repeat signals from the extractor and manual writes). Not part of the active methodology — promoted into edges/spec/work by a separate tool.",
				wiredBoard: null,
				methodologyInstance: TaskBoardMeta.UtilityWorld,
				declaredRole: "corpus",
				ct: ct);
		}
		catch (Exception ex)
		{
			log?.LogWarning(ex, "observations board seed failed for project {ProjectKey}", projectKey);
		}
	}
}
