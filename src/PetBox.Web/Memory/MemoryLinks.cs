using PetBox.Core.Data;
using PetBox.Memory.Contract;

namespace PetBox.Web.Memory;

// The ONE builder of a stable, shareable URL for a memory entry (spec: memory-entry-url):
//
//   /ui/{ws}/{container}/memory/{store}#{key}
//
// The store page renders each entry card with `id={key}`, so the fragment lands the browser on the
// card and `:target` highlights it (app.css `.memory-entry:target`). The URL is pure addressing —
// it survives a reload and a paste into another agent's context; it depends on no client state.
//
// Scope is carried by the CONTAINER segment, not a query flag: a project's memory lives under the
// project key, a workspace's shared memory under its reserved container project
// ("$workspace" / "$ws-{ws}" — WorkspaceMemory.ContainerKeyFor). Both are the same route, so
// workspace-scope entries are addressable through the same UI entry point.
//
// SENSITIVE stores get NO automatic link (MemoryStores.IsSensitive — "ops" has held secrets): the
// builder returns null and every caller must treat null as "render the key as plain text". The
// anchor itself still exists on the store page (a human who is already there may deep-link by hand);
// what is refused is the MACHINE-generated pointer that would pull the entry into agent context.
public static class MemoryLinks
{
	// A project-scope entry URL, or null when the store is sensitive (or the inputs are empty).
	public static string? ProjectEntry(string workspaceKey, string projectKey, string store, string key) =>
		Entry(workspaceKey, projectKey, store, key);

	// A workspace-scope entry URL (the shared memory container of `workspaceKey`), or null when the
	// store is sensitive.
	public static string? WorkspaceEntry(string workspaceKey, string store, string key) =>
		string.IsNullOrWhiteSpace(workspaceKey)
			? null
			: Entry(workspaceKey, WorkspaceMemory.ContainerKeyFor(workspaceKey), store, key);

	// The single URL shape. `containerKey` is the project key OR the workspace container key.
	static string? Entry(string workspaceKey, string containerKey, string store, string key)
	{
		if (string.IsNullOrWhiteSpace(workspaceKey) || string.IsNullOrWhiteSpace(containerKey)
			|| string.IsNullOrWhiteSpace(store) || string.IsNullOrWhiteSpace(key))
			return null;
		if (MemoryStores.IsSensitive(store))
			return null;
		return $"{Routes.ProjectMemoryStore(workspaceKey, containerKey, store)}#{key}";
	}
}
