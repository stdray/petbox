using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Models;

namespace PetBox.Core.Data;

// Per-workspace memory container project keys. Shared cross-project memory is NOT a single
// global "$workspace" for the whole instance — each workspace owns its own container so
// facts cannot leak across tenants.
//
// Scheme (zero-migration for prod $system data):
//   WorkspaceContainerFor("$system") = "$workspace"          // seeded by M028/M031
//   WorkspaceContainerFor(wsKey)     = "$ws-" + wsKey        // lazy ensure on first resolve
//
// Reserved project keys that must never be deleted / orphan-swept: "$system", "$workspace",
// and every "$ws-*" container. See ProjectDeletion.IsReserved.
//
// Workspace keys that become URL segments and `{key}.db` paths MUST be allowlisted at
// creation time (IsCreatableWorkspaceKey). ContainerKeyFor / Routes.SharedMemory are on the
// layout render path — they never throw on a weird key (would 500 every page of that ws).
public static partial class WorkspaceMemory
{
	public const string SystemWorkspace = "$system";
	public const string SystemContainer = "$workspace";
	public const string ContainerPrefix = "$ws-";

	// New workspace keys: safe for Windows file names, URL path segments, and `$ws-` prefixing.
	// `$system` is seeded, never creatable. `sys` is reserved (legacy admin collision).
	// Pattern: ^[a-z0-9][a-z0-9-]*$ (length 1..63).
	[GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,62}$")]
	private static partial Regex CreatableKeyRx();

	// True for a key that may be created via admin UI (not `$system` / `sys`).
	public static bool IsCreatableWorkspaceKey(string? key) =>
		!string.IsNullOrEmpty(key)
		&& CreatableKeyRx().IsMatch(key)
		&& !string.Equals(key, "sys", StringComparison.OrdinalIgnoreCase);

	// True for any known-good workspace key: the reserved system key or a creatable key.
	// Used by EnsureContainerAsync so garbage keys never become Projects rows / files.
	public static bool IsValidWorkspaceKey(string? key) =>
		string.Equals(key, SystemWorkspace, StringComparison.Ordinal)
		|| IsCreatableWorkspaceKey(key);

	// Map a workspace key onto its memory-container project key.
	// Never throws — this is on the layout render path (Routes.SharedMemory / _Layout).
	public static string ContainerKeyFor(string workspaceKey) =>
		string.Equals(workspaceKey, SystemWorkspace, StringComparison.Ordinal)
			? SystemContainer
			: ContainerPrefix + workspaceKey;

	// True for the reserved memory-container project keys ($workspace and $ws-*).
	// "$system" is reserved too but is a real user-facing project, not a memory container.
	public static bool IsWorkspaceContainer(string projectKey) =>
		string.Equals(projectKey, SystemContainer, StringComparison.Ordinal)
		|| projectKey.StartsWith(ContainerPrefix, StringComparison.Ordinal);

	// Inverse of ContainerKeyFor for known containers; null when the key is not one.
	public static string? WorkspaceKeyOfContainer(string containerKey)
	{
		if (string.Equals(containerKey, SystemContainer, StringComparison.Ordinal))
			return SystemWorkspace;
		if (containerKey.StartsWith(ContainerPrefix, StringComparison.Ordinal))
			return containerKey[ContainerPrefix.Length..];
		return null;
	}

	// Lazy-ensure the Projects row for a workspace's memory container (same shape as M028).
	// No-op for invalid keys (never throw — dashboard/layout may call this). Idempotent:
	// existence check + insert. A concurrent insert race is swallowed only when the row
	// now exists (true PK conflict); any other DbException is rethrown.
	public static async Task EnsureContainerAsync(PetBoxDb db, string workspaceKey, CancellationToken ct = default)
	{
		if (!IsValidWorkspaceKey(workspaceKey)) return;
		var key = ContainerKeyFor(workspaceKey);
		if (await db.Projects.AnyAsync(p => p.Key == key, ct)) return;
		try
		{
			await db.InsertAsync(new Project
			{
				Key = key,
				WorkspaceKey = workspaceKey,
				Name = "Workspace",
				Description = "Built-in container for cross-project shared memory",
			}, token: ct);
		}
		catch (System.Data.Common.DbException)
		{
			// Concurrent first-resolve may have won the insert — only swallow if the row is there.
			if (!await db.Projects.AnyAsync(p => p.Key == key, ct)) throw;
		}
	}

	// Resolve the caller's project → its WorkspaceKey → container key, ensuring the row exists.
	// `projectKey` is the already-resolved (claim-authorized) project, not a container.
	public static async Task<string> ResolveAndEnsureContainerAsync(
		PetBoxDb db, string projectKey, CancellationToken ct = default)
	{
		var wsKey = await db.Projects
			.Where(p => p.Key == projectKey)
			.Select(p => p.WorkspaceKey)
			.FirstOrDefaultAsync(ct)
			?? throw new InvalidOperationException($"project '{projectKey}' not found");
		await EnsureContainerAsync(db, wsKey, ct);
		return ContainerKeyFor(wsKey);
	}
}
