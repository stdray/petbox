using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PetBox.Web;

namespace PetBox.Tests.Architecture;

// Guard for the host-composition contract (spec: host-composition-contract): the host offers a
// CONFIGURATION way to not run its background services (Host:BackgroundServices), so no consumer
// has to perform surgery on the DI container. That guarantee only holds if EVERY hosted service
// goes through the gate — one raw builder.Services.AddHostedService<T>() in Program.cs and T would
// keep ticking with the key set to false, silently reintroducing the hazard the fixtures used to
// delete descriptors for.
//
// Unlike its neighbours this is not a NetArchTest rule: NetArchTest reasons about type→type
// dependencies, and "did Program.cs call this particular extension method" is not one (every
// project depends on Microsoft.Extensions.DependencyInjection). Composing Program.ConfigureServices
// for real and inspecting what IHostedService actually resolves to is both expressible and
// strictly stronger — it catches an escaped registration wherever in the composition it hides.
public sealed class HostedServiceGateTests
{
	// The 14 background services Program.cs registers with every feature flag on. The count is
	// asserted, not just the gating: a new hosted service must be a deliberate edit here.
	// (14th: KeyStatFlusher — spec apikey-last-used, batches ApiKeys.LastUsedAt every ~5 min.)
	const int ExpectedHostedServiceCount = 14;

	static ServiceProvider BuildHostServices()
	{
		var builder = WebApplication.CreateBuilder();
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			// Temp store: ConfigureServices ensures the data directory exists.
			["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
			// Every module on, so every conditionally-registered hosted service is present.
			["Features:Config"] = "true",
			["Features:Logging"] = "true",
			["Features:Data"] = "true",
			["Features:Dashboard"] = "true",
			["Features:Tasks"] = "true",
			["Features:Memory"] = "true",
			["Features:LlmRouter"] = "true",
			["Features:Deploy"] = "true",
			// …including SystemLogFlusher, which hangs off the self-log switch.
			["Seq:SelfLog:Enabled"] = "true",
		});

		Program.ConfigureServices(builder);
		return builder.Services.BuildServiceProvider();
	}

	[Fact]
	public void EveryHostedService_GoesThroughTheGate()
	{
		using var sp = BuildHostServices();

		// Resolving is safe and is the whole point: a GATED registration constructs only the
		// wrapper (the real service is materialized inside StartAsync, and only if the key is on),
		// while a raw AddHostedService<T>() constructs T right here — and shows up as T below.
		//
		// Only PETBOX-owned hosted services are in scope. The framework registers its own
		// (DataProtection's key-ring service, the MCP SDK's session idle tracker); those belong to
		// the libraries, are not what "background services" means in the spec, and must keep running
		// — a test host still needs its data-protection keys and its MCP session bookkeeping.
		// GatedHostedService<T> itself lives in PetBox.Web, so the gated ones stay in the set.
		var hosted = sp.GetServices<IHostedService>()
			.Where(h => h.GetType().Assembly.GetName().Name?.StartsWith("PetBox", StringComparison.Ordinal) == true)
			.ToList();

		hosted.Should().HaveCount(ExpectedHostedServiceCount);

		var raw = hosted.Where(h => h is not IGatedHostedService).Select(h => h.GetType().Name).ToList();
		raw.Should().BeEmpty(
			"Program.cs must register background services with AddGatedHostedService so that "
			+ $"{HostedServiceGate.ConfigKey}=false can stop them; raw AddHostedService offenders: "
			+ string.Join(", ", raw));
	}
}
