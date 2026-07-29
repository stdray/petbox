using PetBox.Core.Contract;
using PetBox.Core.Services;

namespace PetBox.Web.AgentDefs;

// Work seed-agent-def-on-project-create: a project created by the server gets the `default` agent
// definition written into its OWN authoritative store, instead of shipping with nothing and
// letting the kit's offline fallback quietly become the normal path (the onboarding run's very
// first line was "no server-side definition for this project yet — using kit default baseline").
// The seeded document is DefaultAgentDefinition.Document, kept 1:1 with the kit's
// DEFAULT_AGENT_DEFINITION by DefaultAgentDefinitionSyncTests.
//
// IT HANGS OFF ProjectDirectory.CreateAsync — the ONE service-layer project writer (the other
// Projects insert in the codebase is WorkspaceMemory's $ws-* container, which is not a user
// project and deliberately gets no roster). That is what makes it fire for the MCP
// `project_create` tool, the admin create page and any future create surface alike, instead of
// living in one endpoint that a second endpoint then forgets.
//
// ITS OWN FILE, mirroring ProjectCanonSeeder: ProjectDirectory is a SINGLETON and
// IAgentDefinitionService is Scoped, so holding one as a constructor dependency would be exactly
// the captive dependency CaptiveDependencyTests fails the build on. CreateAsync rents a scope per
// call instead.
public interface IProjectAgentDefSeeder
{
	Task SeedAsync(string projectKey, CancellationToken ct = default);
}

public sealed class ProjectAgentDefSeeder(
	IAgentDefinitionService defs, ILogger<ProjectAgentDefSeeder>? log = null)
	: IProjectAgentDefSeeder
{
	// Best-effort, NEVER throws: the project ROW is already committed by the time this runs
	// (same contract as the canon seed), so a store hiccup must not turn into a refused creation.
	//
	// NEVER OVERWRITES — belt AND braces, because this is the card's named risk (the curated
	// `$system/default` must survive every scenario, including a repeated bootstrap):
	//   1. An explicit probe: a project that ALREADY has a `default` definition is left alone and
	//      the seed returns without a write. This is what keeps a re-run quiet — no exception, no
	//      log line, no phantom revision.
	//   2. Version 0 on the write itself, which means "create — nothing to clobber"
	//      (TemporalStore.UpsertAsync). If a concurrent writer wins the gap between the probe and
	//      the write, the upsert is classified Stale and REFUSED rather than applied; an identical
	//      payload is a no-op. So the no-overwrite property holds by construction even if the
	//      probe's answer is out of date by the time it is used.
	public async Task SeedAsync(string projectKey, CancellationToken ct = default)
	{
		try
		{
			var existing = await defs.GetAsync(projectKey, DefaultAgentDefinition.Key, ct);
			if (existing is not null) return;

			await defs.UpsertAsync(projectKey, DefaultAgentDefinition.Key,
				DefaultAgentDefinition.Document, version: 0, ct);
		}
		catch (Exception ex)
		{
			log?.LogWarning(ex,
				"default agent definition seed failed for project {ProjectKey} (project creation still succeeds)",
				projectKey);
		}
	}
}
