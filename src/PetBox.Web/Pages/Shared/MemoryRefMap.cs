using System.Security.Claims;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Memory.Contract;
using PetBox.Web.Memory;
using PetBox.Web.Rendering;

namespace PetBox.Web.Pages.Shared;

// Bridges the memory service to the markdown renderer for memory-key mentions
// (memory-key-mention-link), the sibling of NodeRefMap: scan the about-to-render bodies for
// candidate keys (`m-<32hex>` / `ac-<12hex>`), batch-resolve them, and build the key→target map the
// renderer consumes. Kept in one place so the board and node-detail pages wire it identically.
//
// Cost: TWO queries per page regardless of how many keys it mentions — one per container (the
// project's own memory, and its workspace's shared memory), each resolving every candidate key at
// once (IMemoryService.ResolveKeysAsync). No per-key lookup exists anywhere on this path.
//
// The four refusals, all resolving to "leave the key as literal text":
//   - NOT FOUND    — no active entry under that key in either container.
//   - AMBIGUOUS    — the key resolves in more than one store, or in both scopes. There is no
//                    tie-break rule that would not be a guess, and a wrong link is worse than none.
//   - SENSITIVE    — the store is marked sensitive (MemoryStores.IsSensitive); the memory service
//                    drops those stores from the candidate set, so such a key looks NOT FOUND here
//                    and can never acquire a link. (A key that lives BOTH in a sensitive store and
//                    in a normal one therefore links to the normal one — the sensitive store is
//                    invisible to this path, so it cannot even make the key "ambiguous".)
//   - CONTAINED    — the workspace leg is a DERIVED container (WorkspaceMemory.ContainerKeyFor), and
//                    a sandboxOnly principal is not entitled to it (SandboxContainment.PermitsAsync;
//                    same predicate, same suppress-the-leg shape as MemoryApi.CanonAsync). In
//                    practice no caller of this path carries that claim today — both pages that call
//                    BuildAsync declare `[Authorize(Policy = "WorkspaceViewer")]`, which names only
//                    the Cookie scheme, so an api-key identity (the only kind that ever carries
//                    `sandbox_only`) is challenged to /Login before it reaches here. The gate is
//                    asked anyway: SandboxContainmentCallSiteGuardTests requires every site that
//                    derives a workspace container and acts on it to ask the predicate, and the nil
//                    exposure today rests on two things a future change could flip without touching
//                    this file (the policy's scheme list, the pages' auth attributes).
public static class MemoryRefMap
{
	public static async Task<IReadOnlyDictionary<string, NodeRefTarget>> BuildAsync(
		IMemoryService memory, ClaimsPrincipal? user, IProjectCatalog? catalog,
		string workspaceKey, string projectKey, IEnumerable<string?> bodies, CancellationToken ct)
	{
		var keys = MemoryRefs.ExtractKeys(bodies);
		if (keys.Count == 0)
			return EmptyMap;

		// The project's own container, plus the workspace's shared memory container — unless this
		// page IS that container's project, in which case one read covers it.
		var wsContainer = WorkspaceMemory.ContainerKeyFor(workspaceKey);
		var sameContainer = string.Equals(wsContainer, projectKey, StringComparison.Ordinal);

		var project = await memory.ResolveKeysAsync(projectKey, keys, ct);

		// THE DERIVED-STORAGE HOP, asked of the shared predicate — same call, same
		// suppress-the-leg-not-the-request shape as MemoryApi.CanonAsync. `catalog` is null-forgiven
		// here rather than typed non-nullable so the two page models can keep the same
		// optional-ctor-param posture _memory already has for bare unit-test construction
		// (TaskBoardModel/TaskBoardNodeModel); PermitsAsync never dereferences it unless
		// SandboxContainment.AppliesTo(user) is true, and nothing on this path can make that true
		// without also supplying a real catalog (see the class comment: an api-key identity — the
		// only kind that ever carries the claim — never reaches this code).
		var workspace = !sameContainer && await SandboxContainment.PermitsAsync(user, wsContainer, catalog!, ct)
			? await memory.ResolveKeysAsync(wsContainer, keys, ct)
			: EmptyResolution;

		var map = new Dictionary<string, NodeRefTarget>(StringComparer.Ordinal);
		foreach (var key in keys)
		{
			project.TryGetValue(key, out var inProject);
			workspace.TryGetValue(key, out var inWorkspace);
			var hits = (inProject?.Count ?? 0) + (inWorkspace?.Count ?? 0);
			if (hits != 1) continue; // not found, or ambiguous → stays literal

			var url = inProject is { Count: 1 }
				? MemoryLinks.ProjectEntry(workspaceKey, projectKey, inProject[0], key)
				: MemoryLinks.WorkspaceEntry(workspaceKey, inWorkspace![0], key);
			if (url is null) continue; // belt and braces: a sensitive store never yields a URL

			var store = inProject is { Count: 1 } ? inProject[0] : inWorkspace![0];
			var scope = inProject is { Count: 1 } ? "project" : "workspace";
			map[key] = new NodeRefTarget(url, $"memory · {scope} · {store}");
		}
		return map.Count == 0 ? EmptyMap : map;
	}

	static readonly IReadOnlyDictionary<string, NodeRefTarget> EmptyMap
		= new Dictionary<string, NodeRefTarget>(StringComparer.Ordinal);

	static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyResolution
		= new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}
