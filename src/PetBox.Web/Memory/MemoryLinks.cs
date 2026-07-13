using PetBox.Core.Data;
using PetBox.Memory.Contract;

namespace PetBox.Web.Memory;

// The ONE builder of a stable, shareable URL for a memory entry (spec: memory-entry-url):
//
//   /ui/{ws}/{container}/memory/{store}?key={key}#{key}
//
// BOTH halves are load-bearing, and the `?key=` half is the fix for a silent no-op that shipped:
// the store page PAGES its entries (40 per page), so a bare `#{key}` fragment landed the browser on
// page 0 and, for any entry past it, the card was simply NOT IN THE DOM — no error, no highlight,
// nothing (~187 of the 227 entries in the live `notes` store were unreachable by their own URL).
// The QUERY makes the server resolve the entry's page (IMemoryService.FindActiveEntryPageAsync)
// and render the page that actually holds the card; the FRAGMENT then scrolls to it and `:target`
// highlights it (app.css `.memory-entry:target`; the page also marks it `data-highlight="true"`).
// The URL stays stable as the store grows — the page NUMBER is derived per request, never baked in.
// A `?pageNum=N` link is a different animal: it addresses a page, not an entry, and drifts.
//
// The URL is pure addressing — it survives a reload and a paste into another agent's context; it
// depends on no client state.
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
	// The query parameter the store page binds to resolve the entry's page (MemoryStoreModel.Key).
	public const string KeyParam = "key";

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
		var path = Routes.ProjectMemoryStore(workspaceKey, containerKey, store);
		return $"{path}?{KeyParam}={Uri.EscapeDataString(key)}#{key}";
	}
}
