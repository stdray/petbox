namespace PetBox.LlmRouter.Contract;

// Admin surface for the router registry (read/write endpoints + routes). Lives in the
// Contract assembly so the MCP/admin adapters depend only on the contract, never on the
// impl (mirrors the consumer-decoupling boundary). Secrets are write-only: api keys go in
// via SetAsync and are never returned by GetAsync (llm-endpoint-security).
public interface ILlmRegistryAdmin
{
	// The registry for the project's workspace, WITHOUT secrets. Empty when none configured.
	Task<LlmRegistry> GetAsync(string projectKey, CancellationToken ct = default);

	// Replace the registry. `apiKeys` maps endpoint Name -> api key; each is stored as an
	// encrypted secret binding. Endpoints absent from the map keep their existing secret.
	// Throws on validation failure (unknown endpoint in a route, bad URL, etc.).
	Task SetAsync(
		string projectKey,
		LlmRegistry registry,
		IReadOnlyDictionary<string, string> apiKeys,
		CancellationToken ct = default);
}
