using System.ComponentModel;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// card mcp-write-degrades-silently-fix, point 3: every write verb that carries a body used to
// warn about client-side \uXXXX-escaping truncation with the word "oversized" and NO number —
// unactionable, because an agent cannot calibrate a batch size against a word. Each of these
// tools must name the SAME wording everywhere (one class of problem — work
// write-verbs-size-limit-still-has-no-number / comments-upsert-size-guidance — one sentence:
// ModuleMcp.SizeGuidanceText), not five drifting ad-hoc estimates.
//
// Updated by work drop-size-number-from-tool-descriptions: the shared sentence used to name a
// literal byte number. Publishing an evidence-derived guidance number in every write tool's
// description (in the agent's context on every call) read as a hard ceiling rather than a
// margin, and on 2026-07-27 caused an agent to skip a routine call over it — see the comment
// above ModuleMcp.WriteCallSizeGuidanceBytes. The number now lives ONLY in the postfactum
// SizeWarningOrNull warning on an already-applied write, where it is diagnostic, not a
// discouragement — so the two invariants below are now: the public text has no byte-count
// number, and the warning's number matches the internal threshold constant.
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

	// The public sentence must not carry a byte-count figure (a thousands-grouped number like
	// "8,000" or "12,000") — see the comment above ModuleMcp.WriteCallSizeGuidanceBytes for why
	// publishing one in every write tool's description backfired. This does NOT forbid every
	// digit — the Cyrillic escape-inflation ratio ("~2.8x", "2.74-2.88x") is a different,
	// non-threshold number the sentence still legitimately states.
	[Fact]
	public void SizeGuidanceText_CarriesNoByteCountNumber()
	{
		ModuleMcp.SizeGuidanceText.Should().NotMatchRegex(@"\d{1,3}(,\d{3})+",
			"the public guidance sentence must not name a byte-count threshold, only the action and reason");
	}

	// Pins the postfactum warning's number to the constant it actually compares against
	// (ModuleMcp.SizeWarningOrNull / WriteCallSizeGuidanceBytes) — the warning is now the ONLY
	// place this number is still surfaced to a caller, so it cannot silently drift from the
	// threshold that produces it.
	[Fact]
	public void SizeWarningOrNull_NumberMatchesTheRuntimeThreshold()
	{
		var ctx = new DefaultHttpContext();
		ctx.Request.ContentLength = ModuleMcp.WriteCallSizeGuidanceBytes + 1;
		var http = new HttpContextAccessor { HttpContext = ctx };

		var warning = ModuleMcp.SizeWarningOrNull(http);

		warning.Should().NotBeNull();
		warning!.Should().Contain(ModuleMcp.WriteCallSizeGuidanceBytes.ToString("N0"));
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
