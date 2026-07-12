namespace PetBox.LlmRouter.Contract;

// THE admin surface over the LEVELLED registry (core.db's llm_endpoints/llm_routes) — the one the
// runtime actually resolves through. It exists because the old ILlmRegistryAdmin wrote somewhere
// else: after the runtime flip, the admin page and the llm_config_* tools were still editing the
// ConfigBindings store, so the owner saved a route, saw "Saved.", and the router kept serving the
// old one. Editing through THIS contract lands in the same tables the resolver reads.
//
// It lives in the Contract assembly (which is dependency-free on purpose) so the Razor page and the
// MCP tools can use it without taking a dependency on the router impl — the consumer-decoupling
// boundary that LlmRouterBoundaryTests enforces. That is also why it speaks `projectKey` and a
// DISPLAY string for the level rather than PetBox.Core's Scope enum: the write target is DERIVED
// from the project inside the impl, never named by a caller, so no caller can aim a write at a
// level it merely READ (the "I edited the inherited $system row and overwrote it for everyone"
// bug has no expressible form here).
//
// Two things it deliberately does NOT do yet (owner's call, not the code's — llm-l5 items 4-6):
//   * OVERRIDE (copy an inherited level into this workspace, keys and all) — a PARTIAL fork is the
//     one thing that must never happen (an endpoint without its key = an unauthenticated call), so
//     until copy-on-write lands, an inheriting workspace is READ-ONLY here rather than
//     half-editable.
//   * a level/inherited/owner shape on llm_config_get — GetAsync keeps returning a plain
//     LlmRegistry, so the MCP contract is unchanged by this fix.

// A route AS STORED: the row's own stable id plus the route. The admin surface addresses a row by
// this id and never by its position in a list — a concurrent edit or a re-sort used to make
// "routes[i] = route" land on a DIFFERENT route than the one on screen.
public sealed record IdentifiedRoute(string Id, LlmRoute Route);

// What the admin surface shows for one project: the level it writes to, the rows, and whether those
// rows are its OWN or INHERITED from a level above (in which case they are read-only here).
public sealed record LlmRegistryView(
	string Level,
	bool Inherited,
	string? InheritedFrom,
	IReadOnlyList<LlmEndpoint> Endpoints,
	IReadOnlyList<IdentifiedRoute> Routes);

public interface ILlmRegistryEditor
{
	// The registry DECLARED at this project's own level — no inheritance, no secrets. Empty when the
	// level declares nothing (even if the project is being served by a level above).
	Task<LlmRegistry> GetAsync(string projectKey, CancellationToken ct = default);

	// Replace this project's own level with `registry`. Routes get fresh ids (a whole-registry
	// replace has no rows to keep identity with). `apiKeys` maps endpoint Name -> plaintext key;
	// an endpoint absent from the map keeps the key it already had AT THIS LEVEL.
	Task SetAsync(
		string projectKey,
		LlmRegistry registry,
		IReadOnlyDictionary<string, string> apiKeys,
		CancellationToken ct = default);

	// The admin view: own vs inherited, routes carrying their stable ids.
	Task<LlmRegistryView> ViewAsync(string projectKey, CancellationToken ct = default);

	// Replace this project's own level, PRESERVING each route's id (a route whose id is blank is a
	// new row and gets one). This is the write behind an edit/delete of a single row.
	Task SaveAsync(
		string projectKey,
		IReadOnlyList<LlmEndpoint> endpoints,
		IReadOnlyList<IdentifiedRoute> routes,
		IReadOnlyDictionary<string, string> apiKeys,
		CancellationToken ct = default);
}
