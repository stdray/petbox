using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PetBox.Web;

namespace PetBox.Tests.Architecture;

// The measured contract chore/tests-env-leak's fix relies on. `WithWebHostBuilder(b =>
// b.UseSetting(...))` sets values on the SAME configuration object a PRE-Build read
// (`builder.Configuration`, before `builder.Build()`) sees — unlike
// `ConfigureAppConfiguration(... AddInMemoryCollection ...)`, whose source is not merged in time
// for that read. Two independent PRE-Build gates in Program.cs are checked here (they are
// different code paths — one gate being UseSetting-visible does not imply the other is), plus the
// POST-Build precedence a currently-passing test (AuthzDeclarationRatchetTests, via
// AuthzSurfaceHost) already depends on. If a future ASP.NET Core hosting change moves any of these
// three facts, THIS test goes red — not tests that merely assumed the fact in passing.
public sealed class ConfigVisibilityContractTests
{
	static WebApplicationFactory<Program> MakeFactory(Action<IWebHostBuilder> configure)
	{
		var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString("cfg-visibility"),
				["Host:BackgroundServices"] = "false",
			}));
			configure(b);
		});

		var cs = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		_ = factory.CreateClient(); // forces Build() + the app pipeline (endpoint data source) to exist
		return factory;
	}

	// Gate #1 (Program.cs:470): `builder.Configuration.GetValue("Seq:SelfLog:Enabled", false)`,
	// read BEFORE builder.Build(), decides whether SystemLoggerProvider (and the self-log flusher)
	// is registered at all.
	[Fact]
	public void UseSetting_IsVisible_AtTheSeqSelfLogPreBuildGate()
	{
		using var factory = MakeFactory(b => b.UseSetting("Seq:SelfLog:Enabled", "true"));

		factory.Services.GetServices<ILoggerProvider>()
			.Any(p => p.GetType().Name == "SystemLoggerProvider")
			.Should().BeTrue(
				"UseSetting must be visible at Program.cs:470's pre-Build builder.Configuration read " +
				"— this is the channel the fix uses instead of a process-global Seq__SelfLog__Enabled env var");
	}

	// Gate #2 (Program.cs, `FeatureFlags(builder.Configuration).IsEnabled(Feature.Logging)` guarding
	// IIngestionPipeline's registration): a DIFFERENT pre-Build read than gate #1, exercised here on
	// its own so this doesn't just re-measure the Seq flag under another name. appsettings.json
	// defaults Features:Logging to true, so only the OFF direction actually proves UseSetting is
	// read (the ON direction would pass even if the setting were invisible).
	[Fact]
	public void UseSetting_IsVisible_AtAFeatureFlagsPreBuildGate()
	{
		using var factory = MakeFactory(b => b.UseSetting("Features:Logging", "false"));

		factory.Services.GetService<IIngestionPipeline>()
			.Should().BeNull(
				"Features:Logging=false via UseSetting must be visible at its own pre-Build FeatureFlags read " +
				"and suppress IIngestionPipeline's registration — proving UseSetting closes the feature-flag " +
				"gate too, not only the Seq:SelfLog:Enabled one");
	}

	// The POST-Build precedence AuthzSurfaceHost pins on: a config source added LAST
	// (ConfigureAppConfiguration, applied after Build() merges every provider) beats an env var that
	// leaked earlier from a sibling test class. Program.cs reads `Seq:SelfLog:Enabled` again after
	// Build() to decide whether POST /api/events/raw is mapped.
	[Fact]
	public void InMemoryAddedLast_BeatsAnEarlierEnvVar_AtThePostBuildRead()
	{
		try
		{
			Environment.SetEnvironmentVariable("Seq__SelfLog__Enabled", "true");
			using var factory = MakeFactory(b => b.ConfigureAppConfiguration((_, cfg) =>
				cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Seq:SelfLog:Enabled"] = "false" })));

			factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
				.OfType<RouteEndpoint>()
				.Any(e => string.Equals(e.RoutePattern.RawText, "/api/events/raw", StringComparison.OrdinalIgnoreCase))
				.Should().BeFalse(
					"an in-memory config source added after an earlier env var must win at the post-Build read " +
					"— this is the pin AuthzSurfaceHost relies on to stay deterministic regardless of test order");
		}
		finally
		{
			Environment.SetEnvironmentVariable("Seq__SelfLog__Enabled", null);
		}
	}
}
