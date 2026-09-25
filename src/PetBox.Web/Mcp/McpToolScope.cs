using System.Collections.Frozen;
using System.Reflection;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// THE ONE DECLARATION OF WHAT SCOPE AN MCP TOOL NEEDS (spec mcp-scope-declared-once).
//
// Before this, a tool's scope lived in three places that were kept in step by hand: an
// unconditional ModuleMcp.AssertScope at the top of its body (the real gate), a prefix table in
// McpToolScopeFilter.ModuleOf (the tools/list trim), and the "Requires …" sentence in its
// Description. The table fell behind the gate for six families before anyone noticed (work
// `mcp-tools-list-ignores-key-scopes`). Now there is ONE attribute per tool, scanned once, and the
// invocation gate (McpToolScopeFilter's call filter), the tools/list trim and whoami's catalog all
// read it. The "Requires …" prose is checked against it by McpToolScopeDeclarationTests.
//
// Three forms, a closed union exactly like TenantDeclarationAttribute (private protected ctor):
//   * [RequiresScope(a, b, …)]    — ALL of them. Order is the order refusals name them in, and
//                                   matches the order the retired AssertScope calls ran.
//   * [RequiresAnyScope(a, b, …)] — at least ONE of them: the unconditional FLOOR of a tool whose
//                                   full requirement depends on an argument (search_reindex: the
//                                   `tier` decides which write scope; the body still asserts the
//                                   rest). The floor is what tools/list and the gate can know
//                                   without reading arguments.
//   * [RequiresNoScope(reason)]   — deliberately none (whoami, tool_describe, share_revoke,
//                                   petbox_report_issue*). Explicit, so "forgot to declare" and
//                                   "needs nothing" can never look the same.
//
// ARGUMENT-DEPENDENT EXTRAS STAY IN THE BODY: tasks_upsert's optional tasks:approve elevation,
// session_search's memory:read on the `q` branch, search_reindex's per-tier write scopes. The
// attribute declares what EVERY call needs; the body may only ever ask for more.
//
// Lookup order mirrors McpTenantEnforcementFilter.Scan: the METHOD's declaration wins, otherwise
// the tool TYPE's. `Inherited = false` so a base class can never declare on a subclass's behalf.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public abstract class ScopeDeclarationAttribute : Attribute
{
	private protected ScopeDeclarationAttribute() { }

	public abstract McpScopeRequirement Requirement { get; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequiresScopeAttribute(params string[] scopes) : ScopeDeclarationAttribute
{
	public IReadOnlyList<string> Scopes { get; } = scopes;

	public override McpScopeRequirement Requirement => new(McpScopeRequirementKind.All, Scopes);
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequiresAnyScopeAttribute(params string[] scopes) : ScopeDeclarationAttribute
{
	public IReadOnlyList<string> Scopes { get; } = scopes;

	public override McpScopeRequirement Requirement => new(McpScopeRequirementKind.Any, Scopes);
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequiresNoScopeAttribute(string reason) : ScopeDeclarationAttribute
{
	public string Reason { get; } = reason;

	public override McpScopeRequirement Requirement => McpScopeRequirement.None;
}

public enum McpScopeRequirementKind
{
	None,
	All,
	Any,
}

public sealed record McpScopeRequirement(McpScopeRequirementKind Kind, IReadOnlyList<string> Scopes)
{
	public static McpScopeRequirement None { get; } = new(McpScopeRequirementKind.None, []);

	public bool IsSatisfiedBy(IReadOnlySet<string> granted) => Kind switch
	{
		McpScopeRequirementKind.None => true,
		McpScopeRequirementKind.All => Scopes.All(granted.Contains),
		McpScopeRequirementKind.Any => Scopes.Any(granted.Contains),
		_ => false,
	};

	// The refusal text. It keeps the exact shape ModuleMcp.AssertScope always used —
	// "ApiKey lacks required scope '<scope>'" naming the FIRST missing scope in declaration order —
	// so an agent (or a test) that already matched on it needs no new case. An any-of floor names its
	// first alternative the same way and then lists the alternatives.
	public string RefusalMessage(IReadOnlySet<string> granted) => Kind switch
	{
		McpScopeRequirementKind.All =>
			$"ApiKey lacks required scope '{Scopes.First(s => !granted.Contains(s))}'",
		McpScopeRequirementKind.Any =>
			$"ApiKey lacks required scope '{Scopes[0]}' (any one of: {string.Join(", ", Scopes.Select(s => $"'{s}'"))})",
		_ => "",
	};
}

static class McpToolScopes
{
	// The module a scope-free tool is grouped under in whoami's catalog.
	public const string CoreModule = "Core";

	// tool name → its declaration; tools that declare nothing are ABSENT (the ratchet test keeps that
	// set empty, and the invocation gate refuses anything absent).
	public static readonly FrozenDictionary<string, McpScopeRequirement> Declared =
		Scan(typeof(McpToolScopes).Assembly);

	// Every tool name the assembly registers, declared or not — what the ratchet sweeps.
	public static IReadOnlyList<string> AllToolNames(Assembly assembly) =>
		[.. ToolMethods(assembly).Select(t => t.Name).Order(StringComparer.Ordinal)];

	internal static FrozenDictionary<string, McpScopeRequirement> Scan(Assembly assembly)
	{
		var found = new Dictionary<string, McpScopeRequirement>(StringComparer.Ordinal);
		foreach (var (name, type, method) in ToolMethods(assembly))
		{
			var declaration = method.GetCustomAttribute<ScopeDeclarationAttribute>(inherit: false)
				?? type.GetCustomAttribute<ScopeDeclarationAttribute>(inherit: false);
			if (declaration is not null) found[name] = declaration.Requirement;
		}

		return found.ToFrozenDictionary(StringComparer.Ordinal);
	}

	// Same reflection as McpOutputSchema.WithSchemaHonestToolsFromAssembly (the registration) and
	// McpTenantEnforcementFilter.Scan: [McpServerToolType] types, [McpServerTool] methods, public and
	// non-public, static and instance.
	static IEnumerable<(string Name, Type Type, MethodInfo Method)> ToolMethods(Assembly assembly)
	{
		foreach (var toolType in assembly.GetTypes())
		{
			if (toolType.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;
			foreach (var method in toolType.GetMethods(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
			{
				if (method.GetCustomAttribute<McpServerToolAttribute>() is not { } tool) continue;
				yield return (tool.Name ?? method.Name, toolType, method);
			}
		}
	}

	// The catalog module(s) a tool belongs to: the ApiKeyScopes catalog's own Module of each scope it
	// declares (so methodology:write groups under Tasks, llm:* under LlmRouter — the grouping the key
	// UI already renders), or CoreModule when it needs none. An any-of tool is listed under every
	// module one of its alternatives belongs to.
	public static IReadOnlyList<string> ModulesOf(McpScopeRequirement requirement)
	{
		if (requirement.Kind == McpScopeRequirementKind.None || requirement.Scopes.Count == 0) return [CoreModule];
		return [.. requirement.Scopes.Select(ModuleOfScope).Distinct(StringComparer.Ordinal)];
	}

	static string ModuleOfScope(string scope) =>
		ApiKeyScopes.All.FirstOrDefault(s => s.Value == scope)?.Module
			?? (scope.IndexOf(':', StringComparison.Ordinal) is var i and > 0 ? scope[..i] : scope);

	// whoami's `modules` block: every module, its catalog scopes with a `granted` flag, and EVERY tool
	// in it — hidden ones included, since the point is that an agent learns a surface exists even when
	// its key cannot use (or see) it. Module order: catalog order, Core last.
	public static IReadOnlyList<WhoAmIModule> Catalog(IReadOnlySet<string> granted)
	{
		var toolsByModule = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var (tool, requirement) in Declared)
			foreach (var module in ModulesOf(requirement))
			{
				if (!toolsByModule.TryGetValue(module, out var list)) toolsByModule[module] = list = [];
				list.Add(tool);
			}

		var moduleOrder = ApiKeyScopes.All.Select(s => s.Module).Distinct(StringComparer.Ordinal)
			.Concat(toolsByModule.Keys.Order(StringComparer.Ordinal))
			.Distinct(StringComparer.Ordinal)
			.Where(m => m != CoreModule)
			.Append(CoreModule);

		var result = new List<WhoAmIModule>();
		foreach (var module in moduleOrder)
		{
			var scopes = ApiKeyScopes.All.Where(s => s.Module == module)
				.Select(s => new WhoAmIScope(s.Value, granted.Contains(s.Value)))
				.ToList();
			var tools = toolsByModule.TryGetValue(module, out var t) ? t.Order(StringComparer.Ordinal).ToList() : [];
			if (scopes.Count == 0 && tools.Count == 0) continue;
			result.Add(new WhoAmIModule(module, scopes, tools));
		}

		return result;
	}
}
