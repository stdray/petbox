using LinqToDB;
using LinqToDB.Async;

namespace PetBox.Core.Data;

// The service door onto the workspace memory CONTAINERS ("$workspace" / "$ws-<key>"): resolving a
// caller's container, lazily materializing its Projects row, and answering the one authorization
// question a container raises — may THIS caller reach it?
//
// WorkspaceMemory (next door) is the static rulebook: it takes a PetBoxDb and knows the key scheme.
// This is the layer that OWNS the connection, so that consumers — the memory_* MCP tools today — ask
// a service instead of opening core.db themselves (AGENTS.md: the database is visible only in the
// service layer).
//
// ReachableByAsync is the security-bearing method. A container is shared memory: it is reachable by
// the keys of the projects OF ITS OWN WORKSPACE, and by nobody else. The predicate lives here, once,
// rather than in each tool that addresses a container — that is the drift this wave exists to end.
public interface IWorkspaceMemoryDirectory
{
	// Lazy-ensure the Projects row for a workspace's OWN memory container (WorkspaceMemory.ContainerKeyFor),
	// given the workspace key itself rather than an already-addressed container key. No-op for an invalid
	// workspace key (never throws — this runs on a page GET). Idempotent under concurrent callers: the
	// underlying insert races on the container's primary key and swallows only a losing PK conflict (see
	// WorkspaceMemory.EnsureContainerAsync) — two overlapping requests both return having ensured the same
	// single row, never two rows and never an unhandled exception on the loser.
	Task EnsureWorkspaceContainerAsync(string workspaceKey, CancellationToken ct = default);

	// A container addressed DIRECTLY (as a projectKey). Its Projects row is lazy — the first write to a
	// fresh workspace's shared memory materializes it — but ONLY when it names a workspace that EXISTS:
	// "$ws-nosuch" must stay a rejection, not become a fresh container row. Returns false when it names
	// no workspace (the caller's own authz/existence check then rejects it).
	Task<bool> EnsureAddressedContainerAsync(string containerKey, CancellationToken ct = default);

	// The caller's (already claim-authorized) project → its workspace → that workspace's container key,
	// ensuring the row exists.
	Task<string> ResolveContainerForProjectAsync(string projectKey, CancellationToken ct = default);

	// May a key whose project claim is `callerProjectKey` reach `containerKey`? True only when both are
	// rows of the SAME workspace. A null/absent claim, an unknown container and an unknown caller
	// project are all false — a container is never reachable "by default".
	Task<bool> ReachableByAsync(string containerKey, string? callerProjectKey, CancellationToken ct = default);
}

public sealed class WorkspaceMemoryDirectory(ICoreDbFactory dbf) : IWorkspaceMemoryDirectory
{
	public async Task EnsureWorkspaceContainerAsync(string workspaceKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		await WorkspaceMemory.EnsureContainerAsync(db, workspaceKey, ct);
	}

	public async Task<bool> EnsureAddressedContainerAsync(string containerKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await WorkspaceMemory.EnsureAddressedContainerAsync(db, containerKey, ct);
	}

	public async Task<string> ResolveContainerForProjectAsync(string projectKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await WorkspaceMemory.ResolveAndEnsureContainerAsync(db, projectKey, ct);
	}

	public async Task<bool> ReachableByAsync(
		string containerKey, string? callerProjectKey, CancellationToken ct = default)
	{
		if (string.IsNullOrEmpty(callerProjectKey)) return false;

		using var db = dbf.Open();

		// Both workspaces in ONE read, and compared here: the container's and the caller's project's.
		var rows = await db.Projects
			.Where(p => p.Key == containerKey || p.Key == callerProjectKey)
			.Select(p => new { p.Key, p.WorkspaceKey })
			.ToListAsync(ct);

		var containerWs = rows.FirstOrDefault(p => p.Key == containerKey)?.WorkspaceKey;
		var callerWs = rows.FirstOrDefault(p => p.Key == callerProjectKey)?.WorkspaceKey;
		return containerWs is not null
			&& callerWs is not null
			&& string.Equals(callerWs, containerWs, StringComparison.Ordinal);
	}
}
