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
// One thing it deliberately does NOT do yet (owner's call, not the code's — llm-l5 item 4):
//   * OVERRIDE (copy an inherited level into this workspace, keys and all) — a PARTIAL fork is the
//     one thing that must never happen (an endpoint without its key = an unauthenticated call), so
//     until copy-on-write lands, an inheriting workspace is READ-ONLY here rather than
//     half-editable.
//
// llm-l5 item 5 — "should llm_config_get expose level/inherited/owner?" — is DECIDED, yes (work
// llm-config-get-level-derivation-trap). It is not a nicety: the level is derived from the project's
// workspace, so without it a caller cannot tell `smoke` (which writes the LIVE `System:$`) from a
// project that writes a level of its own. LlmRegistryDeclaration carries Level and ServedBy, and the
// MCP results surface both. The INHERITANCE MODEL itself is unchanged — this exposes where a write
// lands, it does not move it.

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

// The registry declared at the level a project reads and writes, TOGETHER WITH that level's CAS
// version and — the part that used to be missing — WHICH LEVEL THAT IS.
//
// `Level` is not decoration. The level is derived from the project's WORKSPACE, so "this project's
// own level" is a phrase with no referent: every project of workspace `$system` — the sandbox
// project `smoke` included — reads and writes `System:$`, the live registry every other workspace
// inherits. A caller that was told only "the registry declared at your own level" had no way to
// learn that before the write landed, and a smoke planned against `smoke` was one call away from
// editing production (work llm-config-get-level-derivation-trap). Level is therefore a REQUIRED
// component of the declaration: nothing can hand a caller a registry without saying where it came
// from and where a write would go.
//
// `Version` 0 means THIS LEVEL has never been written. It does NOT mean the project has no registry:
// `ServedBy` then names the level that actually serves it through inheritance (null when the level
// declares something of its own, or when nothing in the chain does). The distinction is load-bearing
// in the other direction too — declaring one row at an inheriting level SHADOWS the inherited level
// WHOLE, for every project of that workspace.
public sealed record LlmRegistryDeclaration(
	LlmRegistry Registry,
	long Version,
	string Level,
	string? ServedBy = null);

public interface ILlmRegistryEditor
{
	// The registry DECLARED at this project's own level — no inheritance, no secrets. Empty when the
	// level declares nothing (even if the project is being served by a level above).
	Task<LlmRegistry> GetAsync(string projectKey, CancellationToken ct = default);

	// The same read, plus the level's CAS version and the NAME of the level it came from — what
	// llm_config_get returns, so that an agent that reads before it writes holds both a baseline and
	// the answer to "what am I about to overwrite?". When the level declares nothing, ServedBy names
	// the level that serves the project instead (see LlmRegistryDeclaration).
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
	//
	// `expectedVersion` is the CAS baseline (work llm-admin-page-no-cas) — the version the caller
	// last read via GetDeclaredAsync. NULL opts out (unchanged behavior for any other caller). Any
	// other value must equal the level's current version or the write is refused with an
	// InvalidOperationException naming the current one, and NOTHING is written. Unlike PatchAsync,
	// this keeps every route's row id — PatchAsync cannot be used here without losing the identity
	// the admin page addresses rows by.
	Task SaveAsync(
		string projectKey,
		IReadOnlyList<LlmEndpoint> endpoints,
		IReadOnlyList<IdentifiedRoute> routes,
		IReadOnlyDictionary<string, string> apiKeys,
		long? expectedVersion = null,
		CancellationToken ct = default);
}
