using System.Text.Json;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// Unit cover for the privacy-safe shaper extraction on the MCP tool span
// (McpTracingFilter.ExtractArgShapers). The contract: FORMS and SIZES only, never values —
// a shaper is set ONLY when its arg is present/non-empty, and the field shape lists NAMES,
// never values, so a status-only transition is distinguishable from a body edit.
public sealed class McpTracingShaperTests
{
	// Serialize an anonymous object into the raw argument shape the filter sees
	// (IDictionary<string, JsonElement>, matching CallToolRequestParams.Arguments).
	static IDictionary<string, JsonElement> Args(object o) =>
		JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;

	static IReadOnlyDictionary<string, object?> Shapers(object o) =>
		McpTracingFilter.ExtractArgShapers(Args(o)).ToDictionary(kv => kv.Key, kv => kv.Value);

	[Fact]
	public void Absent_Args_Yield_No_Tags()
	{
		McpTracingFilter.ExtractArgShapers(null).Should().BeEmpty();
		McpTracingFilter.ExtractArgShapers(new Dictionary<string, JsonElement>()).Should().BeEmpty();
	}

	[Fact]
	public void Routing_Only_Args_Yield_No_Shapers()
	{
		// A plain read (no q/bodyLen/limit/nodes) sets none of the shaper tags.
		Shapers(new { projectKey = "p", board = "work" }).Should().BeEmpty();
	}

	[Fact]
	public void Q_Present_NonEmpty_Sets_Bool_True()
	{
		Shapers(new { projectKey = "p", q = "needle" })
			.Should().ContainKey("petbox.arg.q").WhoseValue.Should().Be(true);
	}

	[Fact]
	public void Q_Empty_Or_Absent_Sets_No_Tag()
	{
		Shapers(new { projectKey = "p", q = "" }).Should().NotContainKey("petbox.arg.q");
		Shapers(new { projectKey = "p" }).Should().NotContainKey("petbox.arg.q");
	}

	[Fact]
	public void BodyLen_Sets_Numeric_Value()
	{
		Shapers(new { projectKey = "p", bodyLen = 240 })
			.Should().ContainKey("petbox.arg.body_len").WhoseValue.Should().Be(240L);

		// The full-body sentinel is a real value, still forms-only.
		Shapers(new { projectKey = "p", bodyLen = -1 })
			.Should().ContainKey("petbox.arg.body_len").WhoseValue.Should().Be(-1L);
	}

	[Fact]
	public void Limit_Sets_Numeric_Value()
	{
		Shapers(new { projectKey = "p", limit = 50 })
			.Should().ContainKey("petbox.arg.limit").WhoseValue.Should().Be(50L);
		Shapers(new { projectKey = "p" }).Should().NotContainKey("petbox.arg.limit");
	}

	[Fact]
	public void Fields_Single_Node_Lists_Sorted_NonEmpty_Names()
	{
		// title/body/status present; empty body-string and null are dropped; names sorted.
		var tags = Shapers(new
		{
			projectKey = "p",
			board = "work",
			nodes = new[] { new { key = "a", title = "Alpha", body = "hello", status = "Todo" } },
		});
		tags.Should().ContainKey("petbox.arg.fields").WhoseValue.Should().Be("body,key,status,title");
	}

	[Fact]
	public void Fields_StatusOnly_Node_Is_Distinguishable_From_Body_Edit()
	{
		// A status transition carries no body/title — the shape says exactly that.
		var statusOnly = Shapers(new
		{
			projectKey = "p",
			nodes = new[] { new { key = "a", status = "Done", version = 3 } },
		});
		statusOnly.Should().ContainKey("petbox.arg.fields").WhoseValue.Should().Be("key,status");

		// A body edit differs — proving the two are tellable apart. `version` is excluded.
		var bodyEdit = Shapers(new
		{
			projectKey = "p",
			nodes = new[] { new { key = "a", body = "new text", version = 3 } },
		});
		bodyEdit.Should().ContainKey("petbox.arg.fields").WhoseValue.Should().Be("body,key");
	}

	[Fact]
	public void Fields_Unions_NonEmpty_Names_Across_Multiple_Nodes()
	{
		var tags = Shapers(new
		{
			projectKey = "p",
			nodes = new object[]
			{
				new { key = "a", status = "Todo" },
				new { key = "b", title = "Bravo", body = "text" },
			},
		});
		// union(deduped, sorted): a has key,status; b has key,title,body.
		tags.Should().ContainKey("petbox.arg.fields").WhoseValue.Should().Be("body,key,status,title");
	}

	[Fact]
	public void Fields_TopLevel_Node_Payload_Is_Recognized()
	{
		// The single-node variant: node fields sit at the top level (no `nodes` array).
		var tags = Shapers(new { projectKey = "p", title = "T", status = "Todo" });
		tags.Should().ContainKey("petbox.arg.fields").WhoseValue.Should().Be("status,title");
	}

	[Fact]
	public void Fields_Empty_Nodes_Array_Sets_No_Tag()
	{
		Shapers(new { projectKey = "p", nodes = Array.Empty<object>() })
			.Should().NotContainKey("petbox.arg.fields");
	}

	[Fact]
	public void All_Shapers_Compose_On_One_Call()
	{
		var tags = Shapers(new
		{
			projectKey = "p",
			q = "find",
			limit = 10,
			bodyLen = 0,
			nodes = new[] { new { key = "a", status = "Done" } },
		});
		tags.Should().ContainKey("petbox.arg.q").WhoseValue.Should().Be(true);
		tags.Should().ContainKey("petbox.arg.limit").WhoseValue.Should().Be(10L);
		tags.Should().ContainKey("petbox.arg.body_len").WhoseValue.Should().Be(0L);
		tags.Should().ContainKey("petbox.arg.fields").WhoseValue.Should().Be("key,status");
	}
}
