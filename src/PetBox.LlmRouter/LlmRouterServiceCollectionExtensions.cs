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

		services.AddScoped<ILlmClient, CapabilityRouter>();
		return services;
	}
}
