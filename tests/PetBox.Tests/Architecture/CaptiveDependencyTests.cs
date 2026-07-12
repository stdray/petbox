using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Web;

namespace PetBox.Tests.Architecture;

// Guard against the CAPTIVE DEPENDENCY: a singleton whose object graph reaches a Scoped service.
// The scoped service is then resolved ONCE, from the root provider, and lives for the process —
// so every request shares it. When that scoped service is (or holds) a DbContext-like connection
// (`PetBoxDb`, `IScopedDbFactory` consumers, `ILlmClient` → `CapabilityRouter` →
// `ILlmRegistryLevelResolver` → `PetBoxDb`), the result is one connection shared by every
// concurrent request — a guaranteed, not probabilistic, race; and it is never disposed.
//
// Nothing else catches this:
//   * `ValidateOnBuild` only inspects ctor signatures — a captive taken inside a FACTORY lambda
//     (`sp => new Foo(sp.GetService<IScopedThing>())`) is invisible to it;
//   * `ValidateScopes` is enabled by the host only when `IsDevelopment()`, and prod is Production
//     while the test fixtures run as "Testing";
//   * unit tests of the offending classes construct them directly with fakes, bypassing DI.
//   So the WIRING itself was validated nowhere. This test validates it.
//
// The mechanism: compose the real production services, build the root provider with
// `ValidateScopes = true`, and force-resolve EVERY Singleton descriptor from the ROOT. A singleton
// whose graph touches Scoped then throws `InvalidOperationException: Cannot resolve scoped service
// '…' from root provider`, naming the offender. This also permanently closes the sibling class
// "scoped service captured in a middleware/singleton ctor".
public sealed class CaptiveDependencyTests
{
	static (IServiceCollection Services, ServiceProvider Root) BuildProductionRoot()
	{
		var builder = WebApplication.CreateBuilder();
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
			// Every module on: a captive must not be able to hide behind a disabled feature.
			["Features:Config"] = "true",
			["Features:Logging"] = "true",
			["Features:Data"] = "true",
			["Features:Dashboard"] = "true",
			["Features:Tasks"] = "true",
			["Features:Memory"] = "true",
			["Features:LlmRouter"] = "true",
			["Features:Deploy"] = "true",
			["Seq:SelfLog:Enabled"] = "true",
		});

		Program.ConfigureServices(builder);
		var root = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
		return (builder.Services, root);
	}

	[Fact]
	public async Task NoSingleton_CapturesAScopedService()
	{
		var (services, root) = BuildProductionRoot();
		// Constructing singletons has side effects (MemoryUsageRecorder starts a drain task in its
		// ctor, the ingestion pipeline spins up channels) — the provider MUST be disposed even when
		// the assertion below fails. DisposeAsync, not Dispose: ChannelIngestionPipeline is
		// IAsyncDisposable-only, and a sync Dispose of the container throws on it.
		await using var provider = root;

		var offenders = new List<string>();
		var skipped = new List<string>();
		var errored = new List<string>();
		var constructedCount = 0;
		foreach (var group in services.Where(d => d.Lifetime == ServiceLifetime.Singleton)
			.GroupBy(d => (d.ServiceType, d.IsKeyedService, Key: d.IsKeyedService ? d.ServiceKey : null)))
		{
			var serviceType = group.Key.ServiceType;
			// An open generic (e.g. ILogger<>) is not resolvable by its definition; its closed
			// constructions are reached through whoever consumes them.
			if (serviceType.IsGenericTypeDefinition)
			{
				skipped.Add($"{serviceType.Name} (open generic)");
				continue;
			}
			// A service type that ALSO has a non-singleton registration would throw on a root
			// resolve for a legitimate reason (the scoped registration itself), not because of a
			// captive — skip it rather than report a false offender.
			if (services.Any(d => d.ServiceType == serviceType && d.Lifetime != ServiceLifetime.Singleton))
			{
				skipped.Add($"{serviceType.Name} (mixed lifetimes)");
				continue;
			}

			try
			{
				// GetServices (not GetService): a multi-registration service type must have EVERY
				// implementation constructed, or a captive in all-but-the-last one slips through.
				var constructed = group.Key.IsKeyedService
					? provider.GetKeyedServices(serviceType, group.Key.Key).ToList()
					: provider.GetServices(serviceType).ToList();
				constructedCount += constructed.Count;
			}
			catch (InvalidOperationException ex) when (IsScopeViolation(ex))
			{
				offenders.Add($"{serviceType.FullName}: {Root(ex).Message}");
			}
			catch (Exception ex) when (!IsScopeViolation(ex))
			{
				// Any OTHER construction failure (a singleton that needs a live network/file, a
				// factory that throws without a request) is not what this test is about.
				errored.Add($"{serviceType.Name}: {ex.GetType().Name}");
			}
		}

		// The guard is only worth its name if it actually CONSTRUCTED the singleton graph; a
		// composition change that made every root resolve throw early would otherwise turn this
		// test green by vacuity.
		constructedCount.Should().BeGreaterThan(50,
			$"the guard must construct the singleton graph to see into it (skipped: {skipped.Count}, "
			+ $"non-scope construction failures: {string.Join(", ", errored)})");

		offenders.Should().BeEmpty(
			"a Singleton must never capture a Scoped service (it would be resolved once, from the root "
			+ "provider, and shared by every concurrent request — and never disposed). Register the "
			+ "singleton against IServiceScopeFactory and create a scope per use instead. Offenders:\n"
			+ string.Join("\n", offenders));
	}

	static bool IsScopeViolation(Exception ex)
	{
		for (var e = ex; e is not null; e = e.InnerException)
			if (e is InvalidOperationException && e.Message.Contains("scoped service", StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}

	static Exception Root(Exception ex)
	{
		var e = ex;
		while (e.InnerException is not null) e = e.InnerException;
		return e;
	}
}
