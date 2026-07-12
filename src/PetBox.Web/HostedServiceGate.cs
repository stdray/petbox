using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PetBox.Web;

// The host's configurational off-switch for its background services (spec:
// host-composition-contract — "the host MUST offer a configuration way to not run background
// services; a consumer of the host SHOULD NOT achieve that by removing registrations from the
// DI container"). Test fixtures used to reach into IServiceCollection and delete every
// IHostedService descriptor; that is composition surgery on someone else's container, and it
// silently breaks the moment a service gains a non-hosted role (see the ChannelIngestionPipeline
// note below). One config key replaces it.
//
// WHY THE GATE IS AT *START*, NOT AT REGISTRATION
// A knob read while ConfigureServices runs would be unreadable in exactly the scenario it exists
// for: under WebApplicationFactory the test's ConfigureAppConfiguration callback runs during
// builder.Build(), i.e. AFTER ConfigureServices has already executed (this is why the connection
// string is resolved lazily — see the comment at Program.cs's ResolveCs). A registration-time read
// would see the appsettings default and force fixtures back to process-global ENV vars
// (Features__Tasks style), which race across xUnit's parallel test classes. So all 13 hosted
// services stay REGISTERED unconditionally, and the gate is consulted in StartAsync — from the
// BUILT provider, whose IConfiguration includes every test override.
//
// KNOWN, INTENDED CONSEQUENCE (do not file this as a bug):
// ChannelIngestionPipeline is registered twice — once as the singleton `IIngestionPipeline`
// (the write side that endpoints call to enqueue log rows) and once as a hosted service (the
// drain side that flushes the channel into SQLite). This gate only suppresses the HOSTED half.
// With Host:BackgroundServices=false, `IIngestionPipeline` therefore still resolves and still
// ACCEPTS writes — they queue in the channel and are never drained, so ingested rows never become
// queryable. That is the intent (a test that neither ingests nor reads log rows does not want a
// drain loop holding pooled SQLite handles); a test that DOES poll for asynchronously-ingested
// rows must leave the key at its default (true).
public static class HostedServiceGate
{
	// Bool, default TRUE — declared explicitly in appsettings.json so the default is visible.
	// Production never sets it; only test fixtures turn it off.
	public const string ConfigKey = "Host:BackgroundServices";

	public static bool BackgroundServicesEnabled(this IConfiguration configuration) =>
		configuration.GetValue(ConfigKey, true);

	/// <summary>
	/// Registers <typeparamref name="T"/> as a hosted service that honours
	/// <c>Host:BackgroundServices</c>: when the key is false StartAsync returns immediately and
	/// <typeparamref name="T"/> is never even constructed.
	/// </summary>
	public static IServiceCollection AddGatedHostedService<T>(this IServiceCollection services)
		where T : class, IHostedService
	{
		// TryAdd, not Add: ChannelIngestionPipeline is already a singleton in its own right
		// (IIngestionPipeline forwards to it) and must stay ONE instance.
		services.TryAddSingleton<T>();
		return services.AddHostedService(sp => new GatedHostedService<T>(sp));
	}

	/// <inheritdoc cref="AddGatedHostedService{T}(IServiceCollection)"/>
	public static IServiceCollection AddGatedHostedService<T>(
		this IServiceCollection services, Func<IServiceProvider, T> implementationFactory)
		where T : class, IHostedService
	{
		services.TryAddSingleton(implementationFactory);
		return services.AddHostedService(sp => new GatedHostedService<T>(sp));
	}
}

// Marker so the architecture test can assert that EVERY IHostedService the host resolves went
// through the gate (a raw AddHostedService would resolve as something else).
interface IGatedHostedService;

// Generic on T on purpose: AddHostedService routes through TryAddEnumerable, which de-duplicates
// by implementation type — a single non-generic wrapper would collapse all 13 registrations into
// one. One closed generic per gated service keeps them distinct.
//
// The wrapper owns no lifetime: T is a DI singleton, so the container disposes it as before. T is
// resolved lazily inside StartAsync so that a closed gate constructs nothing at all.
sealed class GatedHostedService<T>(IServiceProvider services) : IHostedService, IGatedHostedService
	where T : class, IHostedService
{
	IHostedService? _inner;

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		if (!services.GetRequiredService<IConfiguration>().BackgroundServicesEnabled())
			return;

		_inner = services.GetRequiredService<T>();
		await _inner.StartAsync(cancellationToken);
	}

	public Task StopAsync(CancellationToken cancellationToken) =>
		_inner?.StopAsync(cancellationToken) ?? Task.CompletedTask;
}
