namespace PetBox.Core.Contract;

// Canonical catalog of harness capability ids a role's requiredCapabilities may name.
// Mirrors src/clients-ts/petbox-wire/src/harness-capabilities.ts (`CAPABILITIES`) — that
// file is kit data (ships with the npm package, not reachable from the C# server at
// runtime), so this is the ONE server-side copy; AgentDefinitionCapabilitiesSyncTests
// reads the .ts source and fails the build the moment the two drift.
//
// This is advisory, not enforced: AgentDefinitionJson.Validate does not reject a
// requiredCapabilities value outside this list (a role may legitimately name a capability
// a future/unknown harness declares, or a value written before this catalog existed). The
// admin-UI form renders this catalog as a checkbox group like ApiKeyScopes.All does for
// scopes — the same source the checkbox group offers, not a second hardcoded copy — and
// preserves any pre-existing value outside it rather than silently dropping it.
public static class AgentDefinitionCapabilities
{
	public const string McpMainSession = "mcp_main_session";
	public const string McpSubagent = "mcp_subagent";
	public const string DynamicModelAtSpawn = "dynamic_model_at_spawn";
	public const string RoleFiles = "role_files";
	public const string BuiltinExploreInheritsModel = "builtin_explore_inherits_model";
	public const string Hooks = "hooks";
	public const string SpawnSubagents = "spawn_subagents";

	public static readonly IReadOnlyList<string> All =
	[
		McpMainSession,
		McpSubagent,
		DynamicModelAtSpawn,
		RoleFiles,
		BuiltinExploreInheritsModel,
		Hooks,
		SpawnSubagents,
	];

	public static readonly IReadOnlySet<string> Set = new HashSet<string>(All, StringComparer.Ordinal);
}
