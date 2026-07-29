using PetBox.Memory.Contract;

namespace PetBox.Web.Memory;

// Card canon-invisible-and-unfed (item 2): a fresh project's canon used to be totally silent —
// nothing told a new agent the store/key/budget convention exists until it stumbled on
// MemoryService.cs's comments. ProjectDirectory.CreateAsync now seeds a ~10-line skeleton here,
// into store `canon` key `index` — the exact container GET /api/memory/{project}/canon
// (MemoryApi.CanonAsync) and the SessionStart wiring hook both read. English, matching every
// other canon-adjacent surface (templates/petbox/SKILL.md, the M028/M031 project-row seeds).
//
// DELIBERATELY ITS OWN FILE, not a method on ProjectDirectory: ProjectDirectory.cs already
// branches on IsWorkspaceContainer( (its create-time key-reservation guard) — a file that BOTH
// derives/branches on a workspace-memory container key AND holds an IMemoryService/MemoryDb door
// is exactly the shape SandboxContainmentCallSiteGuardTests treats as a site requiring
// SandboxContainment.Permits*Async, regardless of whether the two actually connect. They do not
// connect here (SeedAsync only ever addresses a real project's OWN canon, never a derived
// container), but the guard is a file-level, not a call-graph, heuristic on purpose — so the
// honest fix is to keep the storage door out of the file that derives container keys, not to
// argue with the sweep or add an allowlist entry (which the guard explicitly forbids for new
// code).
public interface IProjectCanonSeeder
{
	Task SeedAsync(string projectKey, CancellationToken ct = default);
}

public sealed class ProjectCanonSeeder(IMemoryService memory, ILogger<ProjectCanonSeeder>? log = null)
	: IProjectCanonSeeder
{
	const string CanonStore = "canon";
	const string CanonKey = "index";
	const string CanonSkeleton =
		"""
		# Canon — this project's curated memory index

		Store `canon`, key `index` — pulled into every agent session's context automatically.
		Keep it a COMPACT INDEX OF POINTERS, not a second knowledge base: short entries that
		link to detail elsewhere (task boards, docs, other memory stores), never prose dumps.

		Hard budget: 10,000 characters. An oversized body is REJECTED, not truncated.

		To update: `memory_upsert` `store:"canon"` `entries:[{key:"index", version:<version from
		your last read>, body:"..."}]` — this key already exists (seeded here), so `version:0`
		will always conflict; read first (`memory_get`/`memory_search`) to get the version.
		`type` is required only when CREATING a brand-new key, not on an edit.

		PetBox memory is PRIMARY; your harness's own local notes/autoindex is secondary —
		durable facts land here via `memory_remember`/`memory_upsert`, never only in local
		files (invisible to `memory_search`, next session, any other agent).

		Promote to canon when a fact keeps getting re-derived, a rule keeps getting repeated
		across sessions, or a gotcha bites twice — PROPOSE it to the owner; curated, not
		autocaptured.

		Start curating here: durable facts, hot gotchas, open threads worth a newcomer's
		first look.
		""";

	// Best-effort, NEVER throws: a memory-layer hiccup (disk, a disabled feature, an unregistered
	// IMemoryService in some hand-wired host) must not turn into a refused project creation — the
	// caller (ProjectDirectory.CreateAsync) has already committed the project ROW by the time this
	// runs.
	//
	// IDEMPOTENT BY CONSTRUCTION, not by a check written here: memory_upsert's Version 0 means
	// "create — nothing to clobber" (TemporalStore.UpsertAsync). A re-run against a key that
	// ALREADY has an active entry (an owner-curated canon, or this same skeleton from an earlier
	// call) either no-ops (identical payload) or is REJECTED as Stale (different payload) — either
	// way nothing is overwritten. This is the card's main risk, closed by construction rather than
	// by a guard that could itself have a gap — see
	// ProjectDirectorySeedsCanonTests.SecondSeedNeverClobbersACuratedCanon.
	public async Task SeedAsync(string projectKey, CancellationToken ct = default)
	{
		try
		{
			await memory.UpsertAsync(projectKey, CanonStore,
				[new MemoryEntryInput { Key = CanonKey, Version = 0, Type = "Reference", Description = "canon skeleton (seeded at project creation)", Body = CanonSkeleton }],
				[], ct: ct);
		}
		catch (Exception ex)
		{
			log?.LogWarning(ex, "canon skeleton seed failed for project {ProjectKey} (project creation still succeeds)", projectKey);
		}
	}
}
