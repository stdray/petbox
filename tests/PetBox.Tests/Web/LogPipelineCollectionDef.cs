using Xunit;

namespace PetBox.Tests.Web;

// ONE host for every LogPipelineFixture consumer. IClassFixture<T> instantiates T once PER
// CLASS, so the six classes that share the fixture TYPE were each booting their own
// WebApplicationFactory (six ~3s boots + six sets of module SQLite files). A collection
// fixture is instantiated once per COLLECTION, so they now share a single host.
//
// Safe to share because no consumer collides with another on the shared $system/default log
// or the shared core db:
//   • LogPipelineTests — the only class that ingests LOG ROWS into $system/default. Its exact
//     before/after count deltas hold because a collection is SERIAL (xUnit parallelizes across
//     collections, never within one), and no sibling writes log entries there: the authz
//     classes' cross-project writes are all rejected (403) before the pipeline is touched, and
//     their happy paths write into their own Guid-suffixed projects.
//   • OtlpMetricsEndpointTests — writes METRIC points (a different table) under unique metric
//     names, and reads them back by name.
//   • OtlpIngestAuthzTests / LogIngestClefAuthzTests / LlmChatEndpointAuthzTests — seed a fresh
//     Guid-suffixed project+key per test and assert only on that pair.
//   • OAuthDiscoveryProbeTests — anonymous, read-only, no state at all.
// Every class's projects/keys/service-keys/messages are Guid-suffixed, so accumulated rows from
// an earlier class stay invisible to a later one — the same isolation the fixture's own comment
// claims, now verified across all six consumers.
// (Named …Def, not …Collection, for the same reason WebAppFactoryCollectionDef is: CA1711.)
[CollectionDefinition(LogPipelineCollectionDef.Name)]
public sealed class LogPipelineCollectionDef : ICollectionFixture<LogPipelineFixture>
{
	public const string Name = "LogPipeline";
}
