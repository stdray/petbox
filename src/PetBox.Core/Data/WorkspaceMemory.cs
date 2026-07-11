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
public static class WorkspaceMemory
{
	public const string SystemWorkspace = "$system";
	public const string SystemContainer = "$workspace";
	public const string ContainerPrefix = "$ws-";

	// Map a workspace key onto its memory-container project key.
	// Rejects empty / path-like keys so a bad wsKey cannot leak into a project key or URL.
	public static string ContainerKeyFor(string workspaceKey)
	{
		ValidateWorkspaceKey(workspaceKey);
		return string.Equals(workspaceKey, SystemWorkspace, StringComparison.Ordinal)
			? SystemContainer
			: ContainerPrefix + workspaceKey;
	}

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
	// Idempotent: existence check + insert. A concurrent insert race is swallowed only when
	// the row now exists (true PK conflict); any other DbException is rethrown.
	public static async Task EnsureContainerAsync(PetBoxDb db, string workspaceKey, CancellationToken ct = default)
	{
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

	// Workspace keys become container project keys and URL segments — reject empty and path junk.
	static void ValidateWorkspaceKey(string workspaceKey)
	{
		if (string.IsNullOrWhiteSpace(workspaceKey))
			throw new ArgumentException("workspaceKey is required", nameof(workspaceKey));
		if (workspaceKey.Contains('/') || workspaceKey.Contains('\\') || workspaceKey.Contains('\0')
			|| workspaceKey.Contains("..", StringComparison.Ordinal))
			throw new ArgumentException($"invalid workspaceKey '{workspaceKey}'", nameof(workspaceKey));
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
