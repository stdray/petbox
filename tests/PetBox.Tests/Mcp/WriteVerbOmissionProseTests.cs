using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// work/patch-vs-put-class-needs-a-mechanical-gate — PART 2 of 2 (prose gate; PART 1, the
// structural gate, is WriteVerbFieldOmissionShapeTests). A DTO can be shaped perfectly and the
// class still bites, because the caller reading the tool description before the call has no way
// to see the DTO — only the words. Precedent: work/describe-verb-prose-apart-from-structure
// (structure/prose kept as two separate acts) and the keyword-containment style
// ToolDescriptionEconomyMechanismTests/WriteVerbSizeGuidanceTests already use for pinning prose,
// generalized here to a specific question: does the tool's description say, IN WORDS, what
// happens to a field the caller omits?
//
// This does not (cannot) verify the prose is true — a human still has to write an honest
// sentence. It verifies the sentence EXISTS, is pinned so it cannot silently regress, and — via
// the KnownFailing rows below — makes an undocumented tool a build failure instead of a card
// nobody files. That is the whole point of a gate over a third manual sweep: the next
// `*_upsert`/`*_update` that goes silent about its own omission semantics breaks a test, not a
// production write three cards later.
public sealed class WriteVerbOmissionProseTests
{
	// tool -> phrases that MUST appear, verbatim, somewhere in the FULL description (head +
	// below-the-fold — a piloted tool's below-fold text still reaches the caller via
	// tool_describe, so it counts same as WriteVerbSizeGuidanceTests/ToolDescriptionEconomyMechanismTests
	// already assume). Each phrase is a single LINE of the source (no embedded newline) so a
	// harmless rewrap cannot silently break the match — pick a different anchor instead of adding
	// a newline into the phrase.
	public static TheoryData<string, string[]> RequiredPhrases() => new()
	{
		// ── reference instances (do not rewrite — card's own "эталоны" list) ──────────────────
		// apikey_update: the best-documented write verb on the whole surface (explicit sentinels).
		{ "apikey_update", ["A field you OMIT is left untouched"] },
		// memory_upsert: the one tool that already answers BOTH questions — edit AND create.
		{ "memory_upsert", ["an omitted field stays UNCHANGED", "On a NEW entry (version 0) omitted fields start empty"] },
		{ "llm_config_upsert", ["An OMITTED top-level part stays UNCHANGED"] },
		// session_upsert: honestly named PUT, not PATCH — the other correct way to resolve the class.
		{ "session_upsert", ["PUT (full snapshot replace)"] },
		// tasks_board_set_wire: the ONE tool on the surface that INVERTS the omit convention —
		// omitted means CLEAR here, not keep. The text says so plainly, so the gate does not ask
		// for a rewrite; it only pins that the (unusual) sentence stays present.
		{ "tasks_board_set_wire", ["Set (or clear, when wiredBoard is omitted)"] },

		// ── fixed by this card (work/patch-vs-put-class-needs-a-mechanical-gate) ──────────────
		// comments_upsert: named the "Умолчания" gap — tags:[] now has an explicit sentinel sentence.
		{ "comments_upsert", ["`tags:[]`", "CLEARS it, omit `tags` to leave it as-is"] },
		// agent_def_upsert: named the "says nothing at all" gap — now states full-replace plainly.
		{ "agent_def_upsert", ["Full-document REPLACE, not a per-field patch", "is GONE, not kept"] },
		// tasks_upsert: named the "silent on create" gap — now answers the on-create question too.
		{ "tasks_upsert", ["on a NEW node (version 0) an omitted field starts empty"] },
		// tasks_methodology_rules_upsert / template_upsert: named the "Replace" language contradicting
		// silence on in-document omission — now both say the same thing explicitly.
		{ "tasks_methodology_rules_upsert", ["Replace means the WHOLE document", "is REMOVED from what gets stored, not left as-is"] },
		{ "tasks_methodology_template_upsert", ["Whole-document REPLACE, same as rules_upsert", "is REMOVED from what gets stored, not left as-is"] },
	};

	[Theory]
	[MemberData(nameof(RequiredPhrases))]
	public void DescriptionNamesTheOmittedFieldFate(string tool, string[] phrases)
	{
		var full = McpToolDescriptions.Full(RegisteredDescription(tool));
		full.Should().ContainAll(phrases,
			$"{tool}'s description must say IN WORDS what happens to an omitted mutable field "
			+ "(work/patch-vs-put-class-needs-a-mechanical-gate) — a caller reading the tool before "
			+ "the call cannot see the DTO's nullability, only the prose.");
	}

	// KNOWN FAILING, LEFT UNFIXED ON THIS BRANCH — deploy_upsert / deploy_node_upsert are BOTH
	// named in the parent card as silent on omission (the "Противоречия"/deploy row: "оба deploy_*
	// молчат"). Fixed by the PARALLEL worker on fix/batch4-deploy-patch-semantics, which also owns
	// their prose; this branch does not touch DeployTools.cs. Listed here — not folded into
	// RequiredPhrases() — so the gap is COUNTED (a Skip is visible in the run, not a silent
	// omission from the table) rather than just absent. When the parallel branch lands its prose
	// fix, replace this Fact with real [InlineData] rows in RequiredPhrases() naming the sentence
	// it adds — leaving this Skip in place after that merge would be the exact silent pass this
	// card exists to prevent.
	[Theory(Skip = "known failing pending fix/batch4-deploy-patch-semantics — see work/patch-vs-put-class-needs-a-mechanical-gate")]
	[InlineData("deploy_upsert")]
	[InlineData("deploy_node_upsert")]
	public void DescriptionNamesTheOmittedFieldFate_Deploy_PendingParallelFix(string tool)
	{
		var full = McpToolDescriptions.Full(RegisteredDescription(tool));
		full.Should().Contain("omit", $"{tool} should eventually say what an omitted field does");
	}

	// The registered [Description] essay for a tool, by its McpServerTool name (mirrors
	// ToolDescriptionEconomyMechanismTests.RegisteredDescription / WriteVerbSizeGuidanceTests.RegisteredDescription).
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
