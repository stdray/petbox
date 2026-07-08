using System.Text.Json.Nodes;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// Regression guard for the -32602 "must match format date-time" failure strict MCP clients
// (Factory Droid / opencode, ajv) hit on any tool returning a timestamp: SQLite returns
// DateTimeKind.Unspecified, serialized zone-less ("2026-07-04T12:58:10.238"), which violates a
// schema-declared format:"date-time". McpOutputSchema.StripDateTimeFormat drops the annotation so
// the value is validated only as a string (draft 2020-12 makes format non-assertive anyway).
public sealed class McpDateTimeFormatStripTests
{
	[Fact]
	public void Strips_date_time_format()
	{
		var node = new JsonObject { ["type"] = "string", ["format"] = "date-time" };
		McpOutputSchema.StripDateTimeFormat(node);
		Assert.False(node.ContainsKey("format"));
		Assert.Equal("string", node["type"]!.GetValue<string>());
	}

	[Fact]
	public void Strips_date_time_format_on_nullable_union_without_touching_type()
	{
		var node = new JsonObject
		{
			["type"] = new JsonArray { "string", "null" },
			["format"] = "date-time",
		};
		McpOutputSchema.StripDateTimeFormat(node);
		Assert.False(node.ContainsKey("format"));
		// nullability (the ["string","null"] union) must survive so NullableAware still prunes it.
		Assert.IsType<JsonArray>(node["type"]);
		Assert.Contains("null", ((JsonArray)node["type"]!).Select(t => t!.GetValue<string>()));
	}

	[Theory]
	[InlineData("uuid")]
	[InlineData("uri")]
	public void Leaves_other_formats_intact(string fmt)
	{
		var node = new JsonObject { ["type"] = "string", ["format"] = fmt };
		McpOutputSchema.StripDateTimeFormat(node);
		Assert.Equal(fmt, node["format"]!.GetValue<string>());
	}

	[Fact]
	public void No_format_is_a_noop()
	{
		var node = new JsonObject { ["type"] = "string" };
		McpOutputSchema.StripDateTimeFormat(node);
		Assert.False(node.ContainsKey("format"));
	}
}
