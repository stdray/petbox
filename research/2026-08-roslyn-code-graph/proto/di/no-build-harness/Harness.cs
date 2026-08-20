using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// No-Build/No-Run DI introspection harness (research task 11-di-introspection, worker B).
//
// Calls PetBox.Web's OWN composition-root entry point, global::Program.ConfigureServices(builder),
// directly on a WebApplicationBuilder - the exact same seam the build-time OpenAPI generator
// (GetDocument.Insider) already relies on being side-effect-safe (see PetBox.Web.csproj comment
// + Program.cs's IsOpenApiDocumentGeneration guard). We never call builder.Build() or app.Run(),
// so:
//   - no IServiceProvider is constructed -> no singleton factory lambda ever runs
//   - no migrations, no hosted services, no Kestrel/ports
//   - the ONE eager disk side effect in ConfigureServices is `Directory.CreateDirectory` for the
//     configured SQLite data dir (Program.cs ~line 158) - so we point ConnectionStrings:PetBox at
//     a throwaway temp directory before calling ConfigureServices, same technique the product's
//     own test fixtures use (TestSchema.NewTempConnectionString) and the OpenAPI doc-gen path uses.
//
// Output: a JSON array of every ServiceDescriptor collected in builder.Services after
// ConfigureServices returns - ServiceType, Lifetime, and which of
// ImplementationType / ImplementationFactory / ImplementationInstance is set.
//
// No top-level statements here on purpose: PetBox.Web itself has a `public partial class Program`
// (top-level-statement entry point, made public so WebApplicationFactory<Program> can name it in
// tests). Top-level statements in THIS file would generate a second `Program` type in this
// compilation and collide (CS0433) with the referenced one - hence an explicit Main below.
internal static class DiDumpEntry
{
	static void Main(string[] args)
	{
		var tempDataDir = Path.Combine(Path.GetTempPath(), "petbox-di-dump-" + Guid.NewGuid().ToString("N")[..8]);

		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			Args = [],
			EnvironmentName = "Production",
		});
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["ConnectionStrings:PetBox"] = $"Data Source={tempDataDir}/petbox.db;Cache=Shared",
		});

		global::Program.ConfigureServices(builder);

		var rows = builder.Services.Select(d => new
		{
			ServiceType = d.ServiceType.FullName ?? d.ServiceType.Name,
			Lifetime = d.Lifetime.ToString(),
			Kind =
				d.ImplementationInstance is not null ? "Instance" :
				d.ImplementationFactory is not null ? "Factory" :
				d.ImplementationType is not null ? "Type" :
				d.IsKeyedService ? "Keyed" : "Unknown",
			ImplementationType = d.ImplementationType?.FullName,
			IsKeyedService = d.IsKeyedService,
			ServiceKey = d.IsKeyedService ? d.ServiceKey?.ToString() : null,
		}).ToList();

		var outPath = args.Length > 0 ? args[0] : "registrations-no-build.json";
		File.WriteAllText(outPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));

		Console.WriteLine($"descriptors={rows.Count}");
		Console.WriteLine("by-kind: " + string.Join(", ", rows.GroupBy(r => r.Kind).Select(g => $"{g.Key}={g.Count()}")));
		Console.WriteLine("by-lifetime: " + string.Join(", ", rows.GroupBy(r => r.Lifetime).Select(g => $"{g.Key}={g.Count()}")));
		Console.WriteLine($"distinct ServiceType: {rows.Select(r => r.ServiceType).Distinct().Count()}");
		Console.WriteLine($"tempDataDir (mkdir side effect only, should exist+empty): {tempDataDir}");
		Console.WriteLine($"tempDataDir exists: {Directory.Exists(tempDataDir)}");
		Console.WriteLine($"wrote: {Path.GetFullPath(outPath)}");
	}
}
