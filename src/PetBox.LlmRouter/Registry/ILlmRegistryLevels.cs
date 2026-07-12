using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;

namespace PetBox.LlmRouter.Registry;

// THE LEVELLED LLM REGISTRY (spec: llm-registry-own-store) — core.db's llm_endpoints/llm_routes.
//
// READ and WRITE are two interfaces on purpose, and the split is the point:
//
//   ILlmRegistryLevelResolver — reads, and CASCADES: it takes a projectKey and finds the level
//                               that serves it (Project -> Workspace -> System).
//   ILlmRegistryLevelAdmin    — writes, and NEVER cascades: every method DEMANDS an explicit
//                               (Scope, ScopeKey). There is no overload that takes a projectKey,
//                               so there is no code path where a write's target could be derived
//                               from a row that was READ through the cascade. "Edit the settings
//                               you see" cannot silently become "overwrite $system for everyone"
//                               — not because a check catches it, but because the API cannot
//                               express it.
//
// The router resolves through ILlmRegistryLevelResolver (CapabilityRouter). The old
// ConfigBindings-backed LlmRegistryStore is off the runtime path and survives one more version only
// because the admin/MCP surface still writes through it.

// One level of the registry. ScopeKey is the workspace key for Scope.Workspace and "$" for
// Scope.System. Scope.Project is RESERVED — the resolver walks it, nothing writes it yet.
public readonly record struct RegistryLevel(Scope Scope, string ScopeKey)
{
	public const string SystemScopeKey = "$";
	public static RegistryLevel System => new(Scope.System, SystemScopeKey);
	public static RegistryLevel Workspace(string workspaceKey) => new(Scope.Workspace, workspaceKey);

	public override string ToString() => $"{Scope}:{ScopeKey}";
}

// What a project resolved to: ONE level, whole — its endpoints, its routes, its keys. Level is
// null when no level in the chain had a single route (Registry is then empty).
//
// ApiKeys is keyed by endpoint name and contains ONLY endpoints that survived key resolution.
// An endpoint whose stored key will not decrypt is not here and is not in Registry.Endpoints
// either: it is DROPPED, together with its routes. The old store did the opposite — it treated an
// undecryptable key as "no key" and called the upstream unauthenticated.
public sealed record ResolvedRegistryLevel(
	RegistryLevel? Level,
	LlmRegistry Registry,
	IReadOnlyDictionary<string, string> ApiKeys,
	bool InheritanceBlocked,
	string ProjectKey,
	string WorkspaceKey)
{
	// The honest message for "this project has nowhere to send a {capability} call". Names the
	// workspace, and says out loud when the reason is that inheritance is switched off — the one
	// thing a quiet fallback would have hidden.
	public string NoRouteMessage(LlmCapability capability) =>
		$"no route for {capability} at workspace '{WorkspaceKey}' (project '{ProjectKey}')" +
		(InheritanceBlocked
			? " — the system registry is not inherited here"
			: " — and the system registry has no route for it either");
}

// Read side (cascading). Used by the router: it needs the keys to call upstreams.
public interface ILlmRegistryLevelResolver
{
	Task<ResolvedRegistryLevel> ResolveAsync(string projectKey, CancellationToken ct = default);
}

// One level's rows as an EDITOR sees them: endpoints (Name is the PK) and routes carrying the
// stable id of their row. The plain LlmRegistry cannot express that id, and without it the admin
// surface had to address a route by its position in the list.
public sealed record LlmLevelSnapshot(
	IReadOnlyList<LlmEndpoint> Endpoints,
	IReadOnlyList<IdentifiedRoute> Routes);

// Write side. Explicit level, always; no cascade, no inheritance, no projectKey.
public interface ILlmRegistryLevelAdmin
{
	// The registry AS DECLARED at exactly this level (no inheritance), WITHOUT secrets. Empty when
	// this level declares nothing.
	Task<LlmRegistry> GetAsync(Scope scope, string scopeKey, CancellationToken ct = default);

	// The same rows, each route with its stable id — what an editor addresses rows by.
	Task<LlmLevelSnapshot> GetSnapshotAsync(Scope scope, string scopeKey, CancellationToken ct = default);

	// Replace this level, PRESERVING each route's id. A route with a blank id is a new row and gets
	// a fresh one. Otherwise identical to SetAsync (which is this, with every id blank).
	Task SetSnapshotAsync(
		Scope scope,
		string scopeKey,
		IReadOnlyList<LlmEndpoint> endpoints,
		IReadOnlyList<IdentifiedRoute> routes,
		IReadOnlyDictionary<string, string> apiKeys,
		long? updatedBy = null,
		CancellationToken ct = default);

	// Replace this level's registry. `apiKeys` maps endpoint Name -> api key; an endpoint absent
	// from the map keeps the key it already had at THIS level (a rename is a new endpoint, and it
	// starts keyless). Routes may only reference endpoints in `registry` — the validator says so,
	// and the composite FK enforces it in the database. Throws on validation failure, and on any
	// scope other than System/Workspace.
	Task SetAsync(
		Scope scope,
		string scopeKey,
		LlmRegistry registry,
		IReadOnlyDictionary<string, string> apiKeys,
		long? updatedBy = null,
		CancellationToken ct = default);
}
