using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PetBox.Core.Json;
using PetBox.Web;

namespace PetBox.Tests.Architecture;

// json-encoder-shared-globally, THE LATCH: the owner's complaint ("Мы не можем чтоль шарить
// настройки сериализации json") was that a global default existed nowhere — every JSON-emitting
// surface reached for its own JsonSerializerOptions (or none), and five times running someone
// forgot the encoder and shipped Cyrillic as \uXXXX. Program.cs now wires ONE relaxed encoder into
// BOTH framework-level JSON pipelines:
//   - ConfigureHttpJsonOptions -> Microsoft.AspNetCore.Http.Json.JsonOptions, read by a minimal-API
//     endpoint's implicit JSON result (a POCO return, Results.Json, TypedResults.Json).
//   - AddRazorPages().AddJsonOptions -> Microsoft.AspNetCore.Mvc.JsonOptions, read by JsonResult's
//     executor whenever SerializerSettings is left null (every `new JsonResult(x)` call site in
//     this repo today).
// This is what closes the hole for FUTURE surfaces: a brand-new Razor handler or minimal-API
// endpoint that returns a POCO/JsonResult automatically inherits this encoder — it cannot
// reintroduce the bug just by existing, only by manually constructing its own
// JsonSerializerOptions (a different, smaller hole — see PetBoxJsonEncoder.SharedOptions's doc
// comment for the mitigation there).
//
// This test does not probe an endpoint (BoardSearchIndexEncodingTests does that, empirically, for
// the JsonResult path) — it probes the MECHANISM: compose the real production DI container and
// assert both IOptions instances actually carry PetBoxJsonEncoder.Relaxed. If a future refactor of
// Program.cs ever drops either wire-up (e.g. someone "simplifies" the AddRazorPages chain, or a
// merge conflict silently resolves it away), this fails immediately and by name — instead of
// waiting for the owner to notice mangled Cyrillic in production for a sixth time.
public sealed class JsonOptionsWiringTests
{
	static ServiceProvider BuildProductionRoot()
	{
		var builder = WebApplication.CreateBuilder();
		builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
			["Features:Tasks"] = "true",
		});

		Program.ConfigureServices(builder);
		return builder.Services.BuildServiceProvider();
	}

	[Fact]
	public void MinimalApi_HttpJsonOptions_CarryTheRelaxedEncoder()
	{
		using var root = BuildProductionRoot();
		var options = root.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value;

		options.SerializerOptions.Encoder.Should().BeSameAs(PetBoxJsonEncoder.Relaxed,
			"ConfigureHttpJsonOptions in Program.cs must wire PetBoxJsonEncoder.Relaxed — this is what "
			+ "a minimal-API endpoint's implicit JSON result (POCO return / Results.Json / "
			+ "TypedResults.Json) reads; losing this silently reopens the \\uXXXX bug for every future "
			+ "minimal-API surface.");
	}

	[Fact]
	public void RazorPages_MvcJsonOptions_CarryTheRelaxedEncoder()
	{
		using var root = BuildProductionRoot();
		var options = root.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value;

		options.JsonSerializerOptions.Encoder.Should().BeSameAs(PetBoxJsonEncoder.Relaxed,
			"AddRazorPages().AddJsonOptions in Program.cs must wire PetBoxJsonEncoder.Relaxed — this is "
			+ "what JsonResult's executor reads whenever SerializerSettings is null (every "
			+ "`new JsonResult(x)` call site in this repo, e.g. TaskBoard's ?handler=SearchIndex and "
			+ "Config's reveal endpoint); losing this silently reopens the \\uXXXX bug for every future "
			+ "Razor Pages JSON surface.");
	}
}
