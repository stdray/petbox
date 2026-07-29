using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// work/mcp-surface-naming-cleanup, wave 1 — SessionTools/CommentTools/RelationTools/DeployTools/
// ReportTools prose+behavior fixes. Mirrors the reflection style already established by
// Tasks/TasksToolContractFrictionTests and Mcp/WriteVerbOmissionProseTests (RegisteredDescription
// pulling a tool's method-level [Description] by McpServerTool name), extended here with a
// PARAMETER-level counterpart because points (4)/(5) of the card require closed sets and defaults
// to live on the parameter itself, not only in the tool essay.
public sealed class McpSurfaceDescriptionAccuracyTests
{
	// Tool descriptions are hard-wrapped; collapse whitespace so a harmless rewrap cannot silently
	// break a match (same helper as TasksToolContractFrictionTests.Flat).
	static string Flat(string? text) => System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ");

	static MethodInfo FindTool(string toolName)
	{
		foreach (var type in typeof(ModuleMcp).Assembly.GetTypes())
			foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
				if (m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName)
					return m;
		throw new InvalidOperationException($"no MCP tool named '{toolName}'");
	}

	// The registered [Description] essay for a tool, by its McpServerTool name.
	static string RegisteredDescription(string toolName) =>
		FindTool(toolName).GetCustomAttribute<DescriptionAttribute>()?.Description
			?? throw new InvalidOperationException($"{toolName} has no [Description]");

	// A single PARAMETER's [Description] on a tool method — distinct from RegisteredDescription
	// (the whole-tool essay) because points (4)/(5) require the closed set / default to be
	// readable off the PARAMETER schema, not just prose above it.
	static string RegisteredParamDescription(string toolName, string paramName)
	{
		var m = FindTool(toolName);
		var p = m.GetParameters().FirstOrDefault(pp => pp.Name == paramName)
			?? throw new InvalidOperationException($"{toolName} has no parameter '{paramName}'");
		return p.GetCustomAttribute<DescriptionAttribute>()?.Description
			?? throw new InvalidOperationException($"{toolName}.{paramName} has no [Description]");
	}

	// ── (1) session_append.messages must not promise session_get's shape ──────────────────────

	[Fact]
	public void SessionAppendDescription_DoesNotClaimSameShapeAsSessionGet_AndStatesTheRealShape()
	{
		var full = Flat(RegisteredParamDescription("session_append", "messages"));

		full.Should().NotContain("the same shape session_get returns",
			"session_get's SessionGetResult.Content is a single joined string, not a per-message array "
			+ "(Contract/McpToolResults.cs), so the old claim was false");
		full.Should().Contain("session_get does NOT return this array shape",
			"the description must say the true shape in words, not just drop the false claim");
		full.Should().Contain("fromOrdinal");
		full.Should().Contain("lastOrdinal");
	}

	// ── (4) relations_list: direction/includeHistory must carry their closed set/default ───────

	[Fact]
	public void RelationsListDescription_Direction_DocumentsClosedSetAndDefault()
	{
		var full = Flat(RegisteredParamDescription("relations_list", "direction"));

		full.Should().Contain("from|to|both", "the closed set must be spelled out on the parameter itself");
		full.Should().Contain("Default both");
	}

	[Fact]
	public void RelationsListDescription_IncludeHistory_DocumentsEffectAndDefault()
	{
		var full = Flat(RegisteredParamDescription("relations_list", "includeHistory"));

		full.Should().Contain("closedAt");
		full.Should().Contain("Default false");
	}

	// ── (5) relations_create: kind / items[].kind must carry the closed relation-kind set ───────

	[Fact]
	public void RelationsCreateDescription_SingleKind_DocumentsClosedSet()
	{
		var full = Flat(RegisteredParamDescription("relations_create", "kind"));

		full.Should().Contain("task_spec|issue_task|idea_spec|blocks|part_of|supersedes",
			"the process-kind vocabulary lives in ValidateRelationKindAsync/runtime.KnownRelationKinds, "
			+ "not just the tool essay");
		full.Should().Contain("relates_to|depends_on|mirrors", "the neutral-kind vocabulary must also be named");
		full.Should().Contain("linkKinds", "declared methodology-instance kinds are part of the closed set too");
	}

	[Fact]
	public void RelationsCreateDescription_ItemsKind_DocumentsClosedSet()
	{
		var full = Flat(RegisteredParamDescription("relations_create", "items"));

		full.Should().Contain("task_spec|issue_task|idea_spec|blocks|part_of|supersedes");
		full.Should().Contain("relates_to|depends_on|mirrors");
	}

	// ── ReferenceParameters_ShareOneFormulation guard (Tasks/TasksToolContractFrictionTests)
	// keeps governing comments_search/comments_upsert/relations_* essay text; nothing here should
	// have touched the shared "a node reference — … both accepted" formulation, only ADDED
	// sentences, so that test is left to its own file rather than duplicated here.

	// ── work/mcp-surface-naming-cleanup wave 3, part 2.1: tasks_search `cursor` vs `limit` ────
	// `cursor` used to say "Keep every other argument identical while paging" while `limit` said
	// it "can be varied freely between pages without changing the pool" — both cannot be true at
	// once. TasksTools.SearchFingerprint (~1153) and KeysetCursor.Decode settle it: the token is
	// fingerprinted on q/board/underNode/status/nodes/commit/statusKind/sort (+ dataVersion in q
	// mode) and explicitly EXCLUDES bodyLen/includeUrl/limit. Both descriptions must now name that
	// same split instead of contradicting each other.

	[Fact]
	public void TasksSearchDescription_Cursor_NamesExactlyWhatTheFingerprintBinds()
	{
		var full = Flat(RegisteredParamDescription("tasks_search", "cursor"));

		full.Should().Contain("`q`, `board`, `underNode`, `status`, `nodes`, `commit`, `statusKind`, and `sort`",
			"the fingerprint ingredients (SearchFingerprint) must be named, not just asserted");
		full.Should().Contain("`bodyLen`, `includeUrl` and `limit` are NOT part of the fingerprint",
			"this is the exact claim `limit`'s own description makes — the two must agree");
	}

	[Fact]
	public void TasksSearchDescription_Limit_AgreesWithCursor_AndDistinguishesTheTwoDepths()
	{
		var full = Flat(RegisteredParamDescription("tasks_search", "limit"));

		full.Should().Contain("Not part of the q-mode cursor fingerprint",
			"must no longer imply the token is bound to it, matching `cursor`'s own description");
		full.Should().Contain("FIXED depth of 50", "TasksService.PagedCandidateDepth (per-leg candidate depth)");
		full.Should().Contain("`poolLimit`", "the response field this constant is easily confused with");
		full.Should().Contain("160", "RerankBudgetSettings.Candidates default — the OTHER number, not 50");
		full.Should().NotContain("a paged read uses a fixed depth (50), so `limit` can be varied freely",
			"the old sentence never explained that poolLimit (seen as 160 live) is a DIFFERENT number");
	}

	// ── work/mcp-surface-naming-cleanup wave 3, part 2.4: set_description `primitive` case ─────
	// MethodologySetDescription.Apply matches on primitive.Trim().ToLowerInvariant() — the
	// parameter description showed only the camelCase spellings with no word on case-sensitivity.

	[Fact]
	public void MethodologySetDescriptionDescription_Primitive_DocumentsCaseInsensitivity()
	{
		var full = Flat(RegisteredParamDescription("tasks_methodology_set_description", "primitive"));

		full.Should().Contain("case-insensit", "MethodologySetDescription.Apply lowercases before matching (line 35)");
	}
}
