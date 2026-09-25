using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// The static half of spec mcp-scope-declared-once: every MCP tool states its scope requirement in
// ONE machine-readable place ([RequiresScope] / [RequiresAnyScope] / [RequiresNoScope], read by
// McpToolScopes), and the prose an agent reads ("Requires …" in the tool's Description) does not
// contradict it. The runtime half — the gate, tools/list and whoami actually honouring the
// declaration — is McpToolScopeFilterTests.
//
// Modelled on AuthzDeclarationRatchetTests (the tenant axis): a ratchet over the reflection sweep,
// an allowlist that only ever shrinks (it starts EMPTY and should stay that way), and a guard that
// the sweep saw anything at all.
public sealed class McpToolScopeDeclarationTests
{
	static readonly Assembly Web = typeof(McpToolScopes).Assembly;

	// Tools allowed to carry no declaration. EMPTY, and it should stay empty: the invocation gate
	// REFUSES an undeclared tool, so a name here is a tool nobody can call. It exists so a genuinely
	// transitional case has a visible, reviewable place to live instead of a silent fail-open.
	static readonly HashSet<string> Allowlist = new(StringComparer.Ordinal);

	// Scopes a tool's Description may name in a "Requires …" clause WITHOUT declaring them, because
	// the body asks for them only on some calls (argument-dependent extras stay in the body — the
	// attribute declares what EVERY call needs).
	static readonly Dictionary<string, string[]> ConditionalExtras = new(StringComparer.Ordinal)
	{
		["session_search"] = [ApiKeyScopes.MemoryRead],  // only the `q` branch (SessionTools)
		["tasks_upsert"] = [ApiKeyScopes.TasksApprove],   // optional elevation (HasScope), never required
	};

	[Fact]
	public void TheSweep_SeesTheSurface()
	{
		McpToolScopes.AllToolNames(Web).Should().HaveCountGreaterThan(80,
			"the sweep mirrors WithSchemaHonestToolsFromAssembly; a near-empty result means it stopped "
			+ "finding tools and every assertion below would pass by vacuity");
	}

	[Fact]
	public void EveryTool_DeclaresItsScope_OrIsOnTheAllowlist()
	{
		var undeclared = McpToolScopes.AllToolNames(Web)
			.Where(t => !McpToolScopes.Declared.ContainsKey(t) && !Allowlist.Contains(t))
			.ToList();

		undeclared.Should().BeEmpty(
			"every MCP tool declares its scope in ONE place: put [RequiresScope(ApiKeyScopes.X)] (all of), "
			+ "[RequiresAnyScope(...)] (an argument-dependent floor) or [RequiresNoScope(\"why\")] on the "
			+ "[McpServerTool] method (or its type). The call gate refuses an undeclared tool, tools/list and "
			+ "whoami's catalog read the same declaration. Undeclared:\n  " + string.Join("\n  ", undeclared));
	}

	[Fact]
	public void StaleAllowlistEntries_AreDeleted()
	{
		var all = McpToolScopes.AllToolNames(Web).ToHashSet(StringComparer.Ordinal);
		Allowlist.Where(t => !all.Contains(t) || McpToolScopes.Declared.ContainsKey(t)).Should().BeEmpty(
			"an allowlisted tool that is now declared, or no longer exists, must leave the list");
	}

	[Fact]
	public void DeclaredScopes_AreCatalogScopes_AndNonEmpty()
	{
		var catalog = ApiKeyScopes.All.Select(s => s.Value).ToHashSet(StringComparer.Ordinal);
		var bad = McpToolScopes.Declared
			.Where(d => d.Value.Kind != McpScopeRequirementKind.None
				&& (d.Value.Scopes.Count == 0 || d.Value.Scopes.Any(s => !catalog.Contains(s))))
			.Select(d => $"{d.Key}: [{string.Join(", ", d.Value.Scopes)}]")
			.ToList();
		bad.Should().BeEmpty("a declared scope that is not in ApiKeyScopes.All can never be granted:\n" + string.Join("\n", bad));
	}

	// Method-level wins over type-level, and a type-level declaration covers every tool in the type —
	// the same lookup order McpTenantEnforcementFilter.Scan uses. Pinned on the probe types below.
	[Fact]
	public void MethodDeclaration_OverridesTypeDeclaration()
	{
		var declared = McpToolScopes.Scan(typeof(McpToolScopeDeclarationTests).Assembly);
		declared["scope_probe_inherits"].Scopes.Should().Equal(ApiKeyScopes.LogsQuery);
		declared["scope_probe_overrides"].Scopes.Should().Equal(ApiKeyScopes.LogsAdmin);
		declared["scope_probe_none"].Kind.Should().Be(McpScopeRequirementKind.None);
	}

	// Per-TOOL visibility, not per-module: the cases the retired prefix table got wrong.
	[Theory]
	[InlineData("tasks:read", "tasks_search", true)]
	[InlineData("tasks:read", "tasks_upsert", false)]
	[InlineData("tasks:write", "tasks_upsert", true)]
	[InlineData("tasks:write", "tasks_board_adopt", false)]            // governance: + methodology:write
	[InlineData("tasks:write,methodology:write", "tasks_board_adopt", true)]
	[InlineData("tasks:write", "tasks_methodology_set_description", true)] // deliberately NOT governance
	[InlineData("memory:read", "memory_upsert", false)]
	[InlineData("memory:write", "search_reindex", true)]                // any-of floor
	[InlineData("tasks:write", "search_reindex", true)]
	[InlineData("memory:read", "search_reindex", false)]
	[InlineData("memory:read", "whoami", true)]
	public void Requirement_DecidesVisibilityPerTool(string scopes, string tool, bool satisfied) =>
		McpToolScopes.Declared[tool].IsSatisfiedBy(scopes.Split(',').ToHashSet(StringComparer.Ordinal))
			.Should().Be(satisfied);

	// The refusal keeps ModuleMcp.AssertScope's exact wording, naming the FIRST missing scope in
	// declaration order (= the order the retired body asserts ran in).
	[Fact]
	public void RefusalMessage_KeepsTheAssertScopeShape()
	{
		var governance = McpToolScopes.Declared["tasks_board_adopt"];
		governance.RefusalMessage(new HashSet<string>()).Should().Be("ApiKey lacks required scope 'tasks:write'");
		governance.RefusalMessage(new HashSet<string> { "tasks:write" })
			.Should().Be("ApiKey lacks required scope 'methodology:write'");
		McpToolScopes.Declared["search_reindex"].RefusalMessage(new HashSet<string>())
			.Should().StartWith("ApiKey lacks required scope 'memory:write'");
	}

	// ── "Requires …" prose vs the declaration ─────────────────────────────────────────────────────
	//
	// The Description is what an agent reads; the declaration is what the gate enforces. They are two
	// texts, so they are checked against each other: every scope a "requires" clause names positively
	// is declared (or a documented conditional extra), and every declared scope is named. A clause runs
	// from "require(s)" to the end of its sentence; a scope written as "NOT x:y" is a negation and is
	// skipped ("Requires tasks:write — and deliberately NOT methodology:write").
	static readonly Regex RequiresClause = new(@"\brequires?\b(?<clause>.*?)(?:\.(?:\s|$)|$)",
		RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

	static IReadOnlyList<string> NamedScopes(string description)
	{
		var named = new List<string>();
		foreach (Match clause in RequiresClause.Matches(description))
		{
			var text = clause.Groups["clause"].Value;
			foreach (var scope in ApiKeyScopes.All.Select(s => s.Value))
				foreach (Match m in Regex.Matches(text, Regex.Escape(scope)))
					if (!text[..m.Index].TrimEnd().EndsWith("NOT", StringComparison.Ordinal))
						named.Add(scope);
		}

		return named.Distinct(StringComparer.Ordinal).ToList();
	}

	static IEnumerable<(string Tool, string Description)> Descriptions()
	{
		foreach (var type in Web.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
			foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
				if (method.GetCustomAttribute<McpServerToolAttribute>() is { } tool)
					yield return (tool.Name ?? method.Name, method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "");
	}

	[Fact]
	public void RequiresProse_DoesNotContradictTheDeclaration()
	{
		var problems = new List<string>();
		foreach (var (tool, description) in Descriptions())
		{
			if (!McpToolScopes.Declared.TryGetValue(tool, out var requirement)) continue; // the ratchet reports it
			var named = NamedScopes(description);
			var extras = ConditionalExtras.TryGetValue(tool, out var e) ? e : [];

			var claimedButUndeclared = named.Where(s => !requirement.Scopes.Contains(s) && !extras.Contains(s)).ToList();
			if (claimedButUndeclared.Count > 0)
				problems.Add($"{tool}: prose requires [{string.Join(", ", claimedButUndeclared)}] the declaration does not");

			var declaredButUnstated = requirement.Scopes.Where(s => !named.Contains(s)).ToList();
			if (declaredButUnstated.Count > 0)
				problems.Add($"{tool}: declares [{string.Join(", ", declaredButUnstated)}] its \"Requires …\" prose never states");
		}

		problems.Should().BeEmpty(
			"a tool's Description is the agent-facing copy of its scope requirement and must agree with the "
			+ "[RequiresScope] declaration the gate enforces:\n" + string.Join("\n", problems));
	}

	[Fact]
	public void TheProseCheck_SeesNegationAndAlternatives()
	{
		NamedScopes("Requires tasks:write — and deliberately NOT methodology:write: prose.")
			.Should().Equal(ApiKeyScopes.TasksWrite);
		NamedScopes("GOVERNANCE: requires tasks:write AND\n methodology:write.")
			.Should().Equal(ApiKeyScopes.TasksWrite, ApiKeyScopes.MethodologyWrite);
		NamedScopes("Requires memory:write / tasks:write for the tiers it resets.")
			.Should().BeEquivalentTo([ApiKeyScopes.MemoryWrite, ApiKeyScopes.TasksWrite]);
		NamedScopes("Required once the project has any instance. A tasks:read key sees it.").Should().BeEmpty();
	}
}

// Probe tool types for MethodDeclaration_OverridesTypeDeclaration. They live in the TEST assembly,
// so the server never registers them (it scans PetBox.Web only).
[McpServerToolType]
[RequiresScope(ApiKeyScopes.LogsQuery)]
public static class ScopeDeclarationProbeTools
{
	[McpServerTool(Name = "scope_probe_inherits")]
	public static string Inherits() => "";

	[RequiresScope(ApiKeyScopes.LogsAdmin)]
	[McpServerTool(Name = "scope_probe_overrides")]
	public static string Overrides() => "";

	[RequiresNoScope("probe")]
	[McpServerTool(Name = "scope_probe_none")]
	public static string None() => "";
}
