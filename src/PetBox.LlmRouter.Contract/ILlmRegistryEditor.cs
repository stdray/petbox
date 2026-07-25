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

// The registry declared at a project's own level TOGETHER WITH the level's CAS version — the pair a
// caller needs to edit safely: what is there, and the baseline to quote back when replacing it.
// Version 0 means the level has never been written (it declares nothing yet).
public sealed record LlmRegistryDeclaration(LlmRegistry Registry, long Version);

public interface ILlmRegistryEditor
{
	// The registry DECLARED at this project's own level — no inheritance, no secrets. Empty when the
	// level declares nothing (even if the project is being served by a level above).
	Task<LlmRegistry> GetAsync(string projectKey, CancellationToken ct = default);

	// The same read, plus the level's CAS version — what llm_config_get returns, so that an agent
	// that reads before it writes always holds a baseline.
	Task<LlmRegistryDeclaration> GetDeclaredAsync(string projectKey, CancellationToken ct = default);

	// THE CHECKED EDIT behind llm_config_upsert. Two things distinguish it from SetAsync:
	//
	//   * OMISSION MEANS "KEEP", not "clear". `endpoints`/`routes` are nullable: null leaves that
	//     part of the level exactly as it is (routes keep their row ids too), an EMPTY list clears
	//     it. This is the memory_upsert/tasks_upsert contract — an omitted field stays unchanged, a
	//     field passed explicitly empty is cleared — and it is here because the opposite cost us a
	//     card: a caller sending only `routes` silently wiped every endpoint, api keys and all.
	//   * `version` is the CAS baseline from GetDeclaredAsync (0 = the level declares nothing yet).
	//     A baseline that is not the level's current version refuses the whole write.
	//
	// The merged registry is validated as a WHOLE before anything is written, so "keep the endpoints,
	// replace the routes" cannot land a route pointing at an endpoint that is not there.
	Task<LlmRegistryDeclaration> PatchAsync(
		string projectKey,
		IReadOnlyList<LlmEndpoint>? endpoints,
		IReadOnlyList<LlmRoute>? routes,
		IReadOnlyDictionary<string, string> apiKeys,
		long version,
		CancellationToken ct = default);

	// Replace this project's own level with `registry`. Routes get fresh ids (a whole-registry
	// replace has no rows to keep identity with). `apiKeys` maps endpoint Name -> plaintext key;
	// an endpoint absent from the map keeps the key it already had AT THIS LEVEL.
	// UNCHECKED: no CAS baseline — the caller is asserting the whole level. Prefer PatchAsync.
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
