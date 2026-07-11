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

		// Registry store is scoped (depends on the scoped PetBoxDb); one instance backs both
		// the admin contract and the impl-internal resolver.
		services.AddScoped<LlmRegistryStore>();
		services.AddScoped<ILlmRegistryAdmin>(sp => sp.GetRequiredService<LlmRegistryStore>());
		services.AddScoped<ILlmRegistryResolver>(sp => sp.GetRequiredService<LlmRegistryStore>());

		// The levelled registry in core.db (llm-registry-own-store): its own tables, a
		// Project->Workspace->System cascade, api keys as columns of the endpoint row. Registered but
		// NOT yet wired to the router — CapabilityRouter still resolves through the ConfigBindings
		// store above. Importing the live data and flipping ILlmRegistryResolver over are separate
		// steps; until then these two are exercised by tests only.
		services.AddScoped<ILlmRegistryLevelResolver, LlmRegistryLevelResolver>();
		services.AddScoped<ILlmRegistryLevelAdmin, LlmRegistryLevelAdmin>();

		services.AddScoped<ILlmClient, CapabilityRouter>();
		return services;
	}
}
