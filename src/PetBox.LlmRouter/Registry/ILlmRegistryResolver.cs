using PetBox.LlmRouter.Contract;

namespace PetBox.LlmRouter.Registry;

// The registry plus resolved (decrypted) api keys per endpoint name. Internal to the impl —
// the router needs the keys to call upstreams; the admin/MCP surface never sees them.
public sealed record ResolvedRegistry(LlmRegistry Registry, IReadOnlyDictionary<string, string> ApiKeys);

// LEGACY. This was the router's read side; it is not any more — CapabilityRouter resolves through
// ILlmRegistryLevelResolver (core.db). Nothing on the runtime path implements or consumes this
// interface today; it is kept for one version alongside LlmRegistryStore, which the admin/MCP
// surface still uses, and goes away with it.
public interface ILlmRegistryResolver
{
	Task<ResolvedRegistry> ResolveAsync(string projectKey, CancellationToken ct = default);
}
