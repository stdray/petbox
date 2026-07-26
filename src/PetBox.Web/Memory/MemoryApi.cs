using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Memory.Contract;
using PetBox.Web.Auth;

namespace PetBox.Web.Memory;

// Non-MCP read surface for the agent memory canon (spec agent-wiring, memory-canon-storage).
// The wiring hooks pull the curated canon index at session start over REST (a shell command
// can't easily speak MCP), the same way the Stop hook pushes sessions via SessionApi. One
// endpoint, project-scoped, returns BOTH the project's canon and the caller's workspace
// canon so a single call arms an agent's context.
//   GET /api/memory/{projectKey}/canon
// Auth mirrors SessionApi: RequireAuthorization("ApiKey"), then assert memory:read and that the
// key's project claim authorizes {projectKey}. Missing canon → the corresponding part is null
// (still 200); an unknown project simply yields null parts, as the sessions API leaves it.
public static class MemoryApi
{
	// The canon convention: store `canon`, entry `index` — the same in every container. The
	// project canon is `index` in the project container; the shared cross-project canon is
	// `index` in the project's workspace container (WorkspaceMemory.ContainerKeyFor — "$workspace"
	// for $system, "$ws-{wsKey}" otherwise). Two containers, one key: the scope is the
	// container, not a key suffix.
	const string CanonStore = "canon";
	const string CanonKey = "index";

	public static void MapMemoryEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/memory/{projectKey}/canon", CanonAsync)
			.Produces<CanonResponse>()
			.RequireAuthorization("ApiKey");
	}

	// The handler opens no database: the canon comes from IMemoryService and the project's workspace
	// from IProjectDirectory (the catalog of projects — see AGENTS.md, "the database is visible only
	// in the service layer"; an endpoint lambda asks a service, it does not call .Open() itself).
	// TENANT: the PROJECT in the route, and only that. The workspace leg of the response is not a second
	// target a caller can aim — it is derived from the project's own row (IProjectDirectory). Declaring
	// `ArgumentOrContainer` here would be wrong for the same reason: this route takes a real project
	// key, never a `$workspace`/`$ws-<key>` container, and the container it reads is the one the
	// project belongs to.
	//
	// "NOT AIMABLE" IS NOT "AUTHORIZED", AND THIS COMMENT USED TO SAY IT WAS. It read "authorizing the
	// project authorizes it" — true for an ordinary key, FALSE for a sandboxOnly one, and that
	// sentence was the whole hole. Being underivable by an attacker says nothing about whether the
	// CALLER may have it: a sandboxOnly key aimed at its own perfectly legitimate sandbox project is
	// allowed by the PEP, and then this handler derived a container the PEP never judged. Measured on
	// production 2026-07-26 with the real smoke key: `GET /api/memory/smoke/canon` -> 200 carrying the
	// `$system` workspace canon (1309 bytes of owner facts) as the ENTIRE body, `project` being null,
	// while `GET /api/memory/$system/canon` and `/api/memory/kpvotes/canon` both -> 403. The gate
	// refused the key everywhere it was aimed and handed over the container anyway.
	[TenantFrom(TenantSource.Route, "projectKey")]
	static async Task<IResult> CanonAsync(
		HttpContext ctx, string projectKey, IMemoryService memory, IProjectDirectory projects,
		IProjectCatalog catalog, CancellationToken ct)
	{
		var scopes = ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "";
		if (!scopes.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Contains(ApiKeyScopes.MemoryRead))
			return TypedResults.Forbid();

		var project = await ReadCanonAsync(memory, projectKey, CanonKey, ct);

		// Workspace leg = the project's own workspace container — never a hardcoded global. An
		// unknown project has no row, so the workspace part simply stays null (still 200), exactly
		// as before.
		CanonPart? workspace = null;
		var wsKey = (await projects.GetAsync(projectKey, ct))?.WorkspaceKey;
		if (wsKey is not null)
		{
			var container = WorkspaceMemory.ContainerKeyFor(wsKey);

			// THE DERIVED-STORAGE HOP, asked of the shared predicate (SandboxContainment; the guard test
			// enumerates this site mechanically, no list is maintained by hand). A sandboxOnly key gets
			// the project leg it is entitled
			// to and NO workspace leg — suppression, not a 403, because the whole response is not
			// forbidden: the project canon is legitimately its own. A null workspace part is already a
			// valid 200 shape (see the note above — a project with no workspace canon returns exactly
			// this), so the wiring hook needs no new contract and simply injects no shared canon.
			if (await SandboxContainment.PermitsAsync(ctx.User, container, catalog, ct))
				workspace = await ReadCanonAsync(memory, container, CanonKey, ct);
		}

		return TypedResults.Ok(new CanonResponse(project, workspace));
	}

	// The active canon entry of a scope, or null when the store or entry is absent. The
	// store-existence guard keeps a missing canon a null part (not a 500) — an unknown
	// project has no store meta row either, so it lands here too.
	static async Task<CanonPart?> ReadCanonAsync(IMemoryService memory, string projectKey, string key, CancellationToken ct)
	{
		if (!await memory.StoreExistsAsync(projectKey, CanonStore, ct))
			return null;
		var entry = (await memory.ListActiveEntriesAsync(projectKey, CanonStore, ct))
			.FirstOrDefault(e => e.Key == key);
		return entry is null ? null : new CanonPart(entry.Body, entry.Updated, entry.Version);
	}
}

// One scope's canon: the raw index body plus its temporal cursor (updatedAt/version), so the
// hook can cache and detect staleness. Null at the response level when the scope has no canon.
public sealed record CanonPart(string Body, DateTime UpdatedAt, long Version);

// GET /api/memory/{projectKey}/canon — the project's canon and its workspace's shared canon;
// either part is null when that scope carries no canon index.
public sealed record CanonResponse(CanonPart? Project, CanonPart? Workspace);
