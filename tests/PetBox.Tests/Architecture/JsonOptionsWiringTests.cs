using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PetBox.Core.Contract;
using PetBox.Core.Json;

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

	// A THIRD surface this file didn't cover: ResponseBudget.CostOf (spec bounded-result-sets) also
	// serializes rows through hand-owned JsonSerializerOptions, to measure what tasks_search /
	// tasks_methodology_get / memory_search / session_search / comments_search actually put on the
	// wire before prefix-cutting. It used to construct its own JsonSerializerDefaults.Web copy with
	// no Encoder set — the exact trap PetBoxJsonEncoder.SharedOptions's doc comment names by name —
	// so it silently measured Cyrillic rows ~1.68x too expensive (each \uXXXX escape = 6 chars for
	// 1) and truncated those five tools' output earlier than the real budget allowed.
	//
	// It does NOT adopt PetBoxJsonEncoder.SharedOptions wholesale (tried that; it regressed two
	// unrelated tests on pure-ASCII fixtures by dropping null-omission — SharedOptions has no
	// DefaultIgnoreCondition, only the real MCP wire options do) — it keeps its OWN
	// JsonSerializerOptions instance (needs null-ignore, SharedOptions doesn't set that) but shares
	// the one thing that actually drifted: the ENCODER. This does not probe the DI container (there
	// is none to probe — ResponseBudget is constructed with `new()` at each MCP tool call site, not
	// resolved) — it pins Encoder BY REFERENCE to PetBoxJsonEncoder.Relaxed, so a future "just
	// inline my own options here with no Encoder" edit fails the build immediately instead of
	// quietly re-diverging a sixth time.
	[Fact]
	public void ResponseBudget_WireJson_CarriesTheRelaxedEncoder()
	{
		ResponseBudget.WireJson.Encoder.Should().BeSameAs(PetBoxJsonEncoder.Relaxed,
			"ResponseBudget.CostOf must measure rows through PetBoxJsonEncoder.Relaxed — the same "
			+ "encoder instance Program.cs's mcpJson is built on top of — not fall back to the "
			+ "default HTML-safe encoder that re-escapes every Cyrillic char to \\uXXXX, inflating "
			+ "the measured row cost and truncating tasks_search / tasks_methodology_get / "
			+ "memory_search / session_search / comments_search earlier than the real wire size "
			+ "warrants.");
	}
}
