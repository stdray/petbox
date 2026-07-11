using System.Text.Json;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// Unit cover for the privacy-safe shaper extraction on the MCP tool span + the ToolCalls
// self-log event (McpTracingFilter.ExtractMarkedArgs / ExtractArgShapers, driven by the
// [LogArg] registry). The contract: FORMS and SIZES only, never values — a shaper is set ONLY
// when its arg is present/non-empty, only when the tool's SIGNATURE marked that param, and the
// field shape lists NAMES, never values, so a status-only transition is distinguishable from a
// body edit.
public sealed class McpTracingShaperTests
{
	// Serialize an anonymous object into the raw argument shape the filter sees
	// (IDictionary<string, JsonElement>, matching CallToolRequestParams.Arguments).
	static IDictionary<string, JsonElement> Args(object o) =>
		JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(o))!;

	// The span tags of a call to `tool` — what McpTracingFilter would SetTag.
	static IReadOnlyDictionary<string, object?> Shapers(string? tool, object o)
	{
		var args = Args(o);
		return McpTracingFilter.ExtractArgShapers(McpTracingFilter.ExtractMarkedArgs(tool, args), args)
			.ToDictionary(kv => kv.Key, kv => kv.Value);
	}

	// The self-log properties of a call to `tool` — Arg_<paramName> verbatim from the signature.
	static IReadOnlyDictionary<string, object?> LogProps(string? tool, object o) =>
		McpTracingFilter.ExtractMarkedArgs(tool, Args(o))
			.ToDictionary(a => a.LogProperty, a => a.Value);

	[Fact]
	public void Absent_Args_Yield_No_Tags()
	{
		McpTracingFilter.ExtractMarkedArgs("memory_search", null).Should().BeEmpty();
		McpTracingFilter.ExtractMarkedArgs("memory_search", new Dictionary<string, JsonElement>())
			.Should().BeEmpty();
	}

	[Fact]
	public void Routing_Only_Args_Yield_No_Shapers()
	{
		// A plain read (no q/bodyLen/limit/nodes) sets none of the shaper tags.
		Shapers("memory_search", new { projectKey = "p", board = "work" }).Should().BeEmpty();
	}

	// --- the registry (variant B): the SIGNATURE decides, and an unmarked tool says nothing ---

	[Fact]
	public void Registry_Maps_A_Known_Tool_To_Its_Marked_Params()
	{
		var marked = McpLoggedArgs.For("memory_search");
		marked.Select(a => a.Name).Should()
			.BeEquivalentTo(["q", "scope", "store", "limit", "bodyLen", "includeUsage"]);
		marked.Single(a => a.Name == "q").Mode.Should().Be(LogArgMode.Presence);
		marked.Single(a => a.Name == "bodyLen").Mode.Should().Be(LogArgMode.Value);

		// store/scope are the only STRING knobs marked Value: their alphabet is fixed by our own
		// contract (a store name, project|workspace), so they answer "where did the fat search
		// look" without carrying anything the caller typed.
		marked.Single(a => a.Name == "store").Mode.Should().Be(LogArgMode.Value);

		// The two telemetry names are precomputed off the signature spelling.
		marked.Single(a => a.Name == "bodyLen").SpanTag.Should().Be("petbox.arg.body_len");
		marked.Single(a => a.Name == "bodyLen").LogProperty.Should().Be("Arg_bodyLen");
		marked.Single(a => a.Name == "includeUsage").SpanTag.Should().Be("petbox.arg.include_usage");

		// session_search brought its own knobs — proof the registry is per-tool, not global.
		McpLoggedArgs.For("session_search").Select(a => a.Name)
			.Should().BeEquivalentTo(["q", "sessions", "hitsPerSession", "fullScan"]);
	}

	[Fact]
	public void Unmarked_Or_Unknown_Tool_Yields_No_Arg_Telemetry()
	{
		// THE security-relevant default. A tool nobody marked up — or one that does not exist —
		// contributes NO arg tags and NO log properties, even when its args are spelled exactly
		// like marked ones elsewhere. Markup is the ONLY thing that opens the gate.
		var args = new { projectKey = "p", q = "secret needle", limit = 5, bodyLen = 240 };

		McpLoggedArgs.For("memory_upsert").Should().BeEmpty();
		McpLoggedArgs.For("no_such_tool").Should().BeEmpty();
		McpLoggedArgs.For(null).Should().BeEmpty();

		Shapers("no_such_tool", args).Should().BeEmpty();
		Shapers(null, args).Should().BeEmpty();
		LogProps("memory_upsert", args).Should().BeEmpty();
	}

	// --- Presence mode: the value NEVER leaves the process ---

	[Fact]
	public void Q_Present_NonEmpty_Sets_Bool_True()
	{
		Shapers("memory_search", new { projectKey = "p", q = "needle" })
			.Should().ContainKey("petbox.arg.q").WhoseValue.Should().Be(true);
	}

	[Fact]
	public void Q_Empty_Or_Absent_Sets_No_Tag()
	{
		Shapers("memory_search", new { projectKey = "p", q = "" }).Should().NotContainKey("petbox.arg.q");
		Shapers("memory_search", new { projectKey = "p" }).Should().NotContainKey("petbox.arg.q");
	}

	[Fact]
	public void Q_Is_Logged_As_A_Bool_Never_As_The_Query_Text()
	{
		var props = LogProps("tasks_search", new { projectKey = "p", q = "secret needle" });
		props.Should().ContainKey("Arg_q").WhoseValue.Should().Be(true);
		props.Values.Should().NotContain("secret needle");
	}

	// --- Value mode: the knob itself, and only when passed ---

	[Fact]
	public void BodyLen_Sets_Numeric_Value()
	{
		Shapers("memory_search", new { projectKey = "p", bodyLen = 240 })
			.Should().ContainKey("petbox.arg.body_len").WhoseValue.Should().Be(240L);

		// The full-body sentinel is a real value, still forms-only.
		Shapers("memory_search", new { projectKey = "p", bodyLen = -1 })
			.Should().ContainKey("petbox.arg.body_len").WhoseValue.Should().Be(-1L);
	}

	[Fact]
	public void Limit_Sets_Numeric_Value()
	{
		Shapers("memory_search", new { projectKey = "p", limit = 50 })
			.Should().ContainKey("petbox.arg.limit").WhoseValue.Should().Be(50L);
		Shapers("memory_search", new { projectKey = "p" }).Should().NotContainKey("petbox.arg.limit");
	}

	[Fact]
	public void Bool_Knob_Logs_Its_Value_When_Passed_And_Nothing_When_Absent()
	{
		LogProps("memory_search", new { projectKey = "p", includeUsage = true })
			.Should().ContainKey("Arg_includeUsage").WhoseValue.Should().Be(true);
		LogProps("memory_search", new { projectKey = "p", includeUsage = false })
			.Should().ContainKey("Arg_includeUsage").WhoseValue.Should().Be(false);
		LogProps("memory_search", new { projectKey = "p" })
			.Should().NotContainKey("Arg_includeUsage");
	}

	[Fact]
	public void Log_Properties_Are_Named_Arg_ParamName_Verbatim()
	{
		var props = LogProps("session_search", new
		{
			projectKey = "p",
			q = "needle",
			sessions = 10,
			hitsPerSession = 5,
			fullScan = true,
		});
		props.Should().BeEquivalentTo(new Dictionary<string, object?>
		{
			["Arg_q"] = true,             // Presence — the text stays private
			["Arg_sessions"] = 10L,
			["Arg_hitsPerSession"] = 5L,
			["Arg_fullScan"] = true,
		});
	}

	// --- the derived `fields` shape (NOT a parameter — read off the node payload) ---

	[Fact]
	public void Fields_Single_Node_Lists_Sorted_NonEmpty_Names()
	{
		// title/body/status present; empty body-string and null are dropped; names sorted.
		var tags = Shapers("tasks_upsert", new
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
		var statusOnly = Shapers("tasks_upsert", new
		{
			projectKey = "p",
			nodes = new[] { new { key = "a", status = "Done", version = 3 } },
		});
		statusOnly.Should().ContainKey("petbox.arg.fields").WhoseValue.Should().Be("key,status");

		// A body edit differs — proving the two are tellable apart. `version` is excluded.
		var bodyEdit = Shapers("tasks_upsert", new
		{
			projectKey = "p",
			nodes = new[] { new { key = "a", body = "new text", version = 3 } },
		});
		bodyEdit.Should().ContainKey("petbox.arg.fields").WhoseValue.Should().Be("body,key");
	}

	[Fact]
	public void Fields_Unions_NonEmpty_Names_Across_Multiple_Nodes()
	{
		var tags = Shapers("tasks_upsert", new
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
		var tags = Shapers("tasks_upsert", new { projectKey = "p", title = "T", status = "Todo" });
		tags.Should().ContainKey("petbox.arg.fields").WhoseValue.Should().Be("status,title");
	}

	[Fact]
	public void Fields_Empty_Nodes_Array_Sets_No_Tag()
	{
		Shapers("tasks_upsert", new { projectKey = "p", nodes = Array.Empty<object>() })
			.Should().NotContainKey("petbox.arg.fields");
	}

	[Fact]
	public void Fields_Survives_On_An_Unmarked_Tool()
	{
		// The field shape is DERIVED, not declared: it does not depend on [LogArg] markup and so
		// keeps working for the upsert tools, which mark no parameters at all.
		Shapers("tasks_upsert", new { projectKey = "p", nodes = new[] { new { key = "a", status = "Done" } } })
			.Should().ContainKey("petbox.arg.fields").WhoseValue.Should().Be("key,status");
	}

	[Fact]
	public void All_Shapers_Compose_On_One_Call()
	{
		var tags = Shapers("tasks_search", new
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
