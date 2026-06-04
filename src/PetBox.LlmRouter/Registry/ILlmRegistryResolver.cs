using PetBox.LlmRouter.Contract;

namespace PetBox.LlmRouter.Registry;

// The registry plus resolved (decrypted) api keys per endpoint name. Internal to the impl —
// the router needs the keys to call upstreams; the admin/MCP surface never sees them.
public sealed record ResolvedRegistry(LlmRegistry Registry, IReadOnlyDictionary<string, string> ApiKeys);

// Read side used by the router: resolve the project's registry WITH secrets. Separate from
// the public ILlmRegistryAdmin (which is secret-free) so the secret-bearing path stays
// inside the impl assembly.
public interface ILlmRegistryResolver
{
	Task<ResolvedRegistry> ResolveAsync(string projectKey, CancellationToken ct = default);
}
