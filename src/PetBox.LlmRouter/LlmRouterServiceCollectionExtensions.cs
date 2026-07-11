using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PetBox.LlmRouter.Contract;
using PetBox.LlmRouter.Http;
using PetBox.LlmRouter.Registry;
using PetBox.LlmRouter.Routing;

namespace PetBox.LlmRouter;

public static class LlmRouterServiceCollectionExtensions
{
	// Wire the LLM router. Registered unconditionally by the host (like the other module
	// services); the Feature flag gates the MCP surface, not DI. Consumers resolve ILlmClient
	// (neutral contract); the admin surface is ILlmRegistryAdmin.
	public static IServiceCollection AddLlmRouter(this IServiceCollection services)
	{
		services.TryAddSingleton(TimeProvider.System);

		// Stateful singletons shared across requests: the breaker's health state and the
		// per-endpoint cert-pinned HttpClient cache must outlive a single scope.
		services.TryAddSingleton<EndpointBreaker>();
		services.TryAddSingleton<CertPinningHttpClientProvider>();
		services.TryAddSingleton<IOpenAiCompatibleClient, OpenAiCompatibleClient>();

		// The levelled registry in core.db (llm-registry-own-store): its own tables, a
		// Project->Workspace->System cascade, api keys as columns of the endpoint row. THIS is what
		// the router resolves through now — the runtime flip. A workspace that declares no registry
		// of its own inherits $system's, which is precisely how semantic search comes alive for the
		// projects that never had an Embed route.
		services.AddScoped<ILlmRegistryLevelResolver, LlmRegistryLevelResolver>();
		services.AddScoped<ILlmRegistryLevelAdmin, LlmRegistryLevelAdmin>();

		// The OLD ConfigBindings-backed store. It is no longer on the router's path: nothing resolves
		// through ILlmRegistryResolver any more. It stays registered for ONE version because the
		// admin/MCP surface (Pages/Llm, LlmRouterTools) still edits through ILlmRegistryAdmin — so for
		// that version the admin UI shows the old registry while the runtime serves the new one. That
		// divergence is deliberate and time-boxed: the admin surface moves to ILlmRegistryLevelAdmin
		// next, and this store is deleted after it.
		services.AddScoped<LlmRegistryStore>();
		services.AddScoped<ILlmRegistryAdmin>(sp => sp.GetRequiredService<LlmRegistryStore>());
		services.AddScoped<ILlmRegistryResolver>(sp => sp.GetRequiredService<LlmRegistryStore>());

		services.AddScoped<ILlmClient, CapabilityRouter>();
		return services;
	}
}
