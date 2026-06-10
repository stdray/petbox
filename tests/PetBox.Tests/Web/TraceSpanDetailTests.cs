using PetBox.Log.Core.Tracing;
using PetBox.Web.Pages.Logs;

namespace PetBox.Tests.Web;

// The expandable span-detail row on the trace waterfall (spec: trace-span-attributes):
// span-level attributes are surfaced on demand, with the constant resource/SDK noise
// (identical on every span of a trace) filtered out, values rendered as plain text.
public sealed class TraceSpanDetailTests
{
	static SpanRecord Span(string? attributesJson) => new()
	{
		SpanId = "s1",
		TraceId = "t1",
		Name = "tasks.upsert",
		AttributesJson = attributesJson!,
	};

	[Fact]
	public void Resource_noise_is_filtered_and_attributes_sorted()
	{
		var attrs = TraceModel.DisplayAttributes(Span("""
			{
				"petbox.project": "$system",
				"telemetry.sdk.version": "1.15.3",
				"service.name": "petbox",
				"service.instance.id": "x",
				"petbox.node_count": 3,
				"http.response.status_code": 200
			}
			"""));

		Assert.Equal(
			new[] { "http.response.status_code", "petbox.node_count", "petbox.project" },
			attrs.Select(a => a.Key));
		Assert.Equal("3", attrs.Single(a => a.Key == "petbox.node_count").Value);
		Assert.Equal("$system", attrs.Single(a => a.Key == "petbox.project").Value);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("not json")]
	[InlineData("[1,2]")]
	public void Missing_or_malformed_attributes_render_as_empty(string? json) =>
		Assert.Empty(TraceModel.DisplayAttributes(Span(json)));

	[Fact]
	public void Kind_and_status_names_match_otlp_enums()
	{
		Assert.Equal("Internal", TraceModel.KindName(0));
		Assert.Equal("Server", TraceModel.KindName(1));
		Assert.Equal("Client", TraceModel.KindName(2));
		Assert.Equal("Unset", TraceModel.StatusName(0));
		Assert.Equal("Error", TraceModel.StatusName(2));
	}
}
