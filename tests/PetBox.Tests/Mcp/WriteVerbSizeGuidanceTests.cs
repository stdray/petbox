using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// card mcp-write-degrades-silently-fix, point 3: every write verb that carries a body used to
// warn about client-side \uXXXX-escaping truncation with the word "oversized" and NO number —
// unactionable, because an agent cannot calibrate a batch size against a word. Each of these
// tools must now name an actual number, and it must be the SAME number everywhere (one class of
// problem — work write-verbs-size-limit-still-has-no-number / comments-upsert-size-guidance —
// one sentence: ModuleMcp.SizeGuidanceText), not five drifting ad-hoc estimates.
public sealed class WriteVerbSizeGuidanceTests
{
	[Theory]
	[InlineData("memory_remember")]
	[InlineData("memory_upsert")]
	[InlineData("tasks_upsert")]
	[InlineData("comments_upsert")]
	[InlineData("session_append")]
	[InlineData("session_upsert")]
	public void WriteVerb_DescriptionCarriesTheSharedSizeGuidanceSentenceVerbatim(string tool)
	{
		var desc = RegisteredDescription(tool);
		desc.Should().Contain(ModuleMcp.SizeGuidanceText,
			$"{tool}'s description should carry the shared size-guidance sentence, not a bespoke one");
	}

	// Pins the description's literal number to the constant the runtime size-warning check
	// (ModuleMcp.SizeWarningOrNull) actually compares against, so the two cannot silently drift
	// apart — a change to one without the other would leave the description lying about the
	// number the server itself uses.
	[Fact]
	public void SizeGuidanceText_NumberMatchesTheRuntimeThreshold()
	{
		ModuleMcp.SizeGuidanceText.Should().Contain(ModuleMcp.WriteCallSizeGuidanceBytes.ToString("N0"));
	}

	// The registered [Description] essay for a tool, by its McpServerTool name (mirrors
	// ToolDescriptionEconomyMechanismTests.RegisteredDescription).
	static string RegisteredDescription(string toolName)
	{
		foreach (var type in typeof(ModuleMcp).Assembly.GetTypes())
			foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
				if (m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName)
					return m.GetCustomAttribute<DescriptionAttribute>()?.Description
						?? throw new InvalidOperationException($"{toolName} has no [Description]");
		throw new InvalidOperationException($"no MCP tool named '{toolName}'");
	}
}
