using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Auth;
using PetBox.Memory.Contract;

namespace PetBox.Web.Memory;

// Non-MCP read surface for the agent memory canon (spec agent-wiring, memory-canon-storage).
// The wiring hooks pull the curated canon index at session start over REST (a shell command
// can't easily speak MCP), the same way the Stop hook pushes sessions via SessionApi. One
// endpoint, project-scoped, returns BOTH the project's canon and the shared workspace canon
// so a single call arms an agent's context.
//   GET /api/memory/{projectKey}/canon
// Auth mirrors SessionApi: RequireAuthorization("ApiKey"), then assert memory:read and that the
// key's project claim authorizes {projectKey}. Missing canon → the corresponding part is null
// (still 200); an unknown project simply yields null parts, as the sessions API leaves it.
public static class MemoryApi
{
	// The canon convention: store `canon`; entry `index` = the project's canon; entry
	// `workspace` = the shared cross-project canon, living in the SAME container the MCP
	// `workspace` memory scope resolves to (MemoryTools.WorkspaceContainer — physically the
	// `$system` project; workspace and $system-project memory share one container, so the
	// two canons are told apart by key, not by container).
	const string CanonStore = "canon";
	const string CanonKey = "index";
	const string CanonWorkspaceKey = "workspace";

	public static void MapMemoryEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/memory/{projectKey}/canon", CanonAsync)
			.Produces<CanonResponse>()
			.RequireAuthorization("ApiKey");
	}

	static async Task<IResult> CanonAsync(
		HttpContext ctx, string projectKey, IMemoryService memory, CancellationToken ct)
	{
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (!ProjectScope.Authorizes(claim, projectKey))
			return TypedResults.Forbid();
		var scopes = ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "";
		if (!scopes.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Contains(ApiKeyScopes.MemoryRead))
			return TypedResults.Forbid();

		var project = await ReadCanonAsync(memory, projectKey, CanonKey, ct);
		var workspace = await ReadCanonAsync(memory, Mcp.MemoryTools.WorkspaceContainer, CanonWorkspaceKey, ct);
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

// GET /api/memory/{projectKey}/canon — the project's canon and the shared workspace canon;
// either part is null when that scope carries no canon index.
public sealed record CanonResponse(CanonPart? Project, CanonPart? Workspace);
