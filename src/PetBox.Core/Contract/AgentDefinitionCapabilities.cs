using JetBrains.Annotations;

namespace PetBox.Core.Contract;

// Canonical catalog of harness capability ids a role's requiredCapabilities may name.
// Mirrors src/clients-ts/petbox-wire/src/harness-capabilities.ts (`CAPABILITIES`) — that
// file is kit data (ships with the npm package, not reachable from the C# server at
// runtime), so this is the ONE server-side copy; AgentDefinitionCapabilitiesSyncTests
// reads the .ts source and fails the build the moment the two drift.
//
// WHERE IT BITES: DefaultAgentDefinition.Validate rejects a requiredCapabilities value on the
// SHIPPED baseline (src/common/default-agents.json) that is not in this list. That scope is
// deliberate. A capability id nothing declares is invisible at write time and fails far away —
// the kit's artifact compiler simply emits no artifact for a role whose requirements no harness
// meets — so on the one document this repo ships it is a build error. Documents that are NOT
// this repo's (a user's or a project's own definition layer on disk) stay free to name a
// capability a future harness declares; the kit validates those, not this.
//
// This pairing is also what keeps the two halves honest after the server-side agent-definition
// store was torn down (work agent-defs-server-teardown): the sync test pins this list to the
// kit's, and DefaultAgentDefinition.Validate pins the shipped roster to this list. Neither half
// is decorative — break either and CI goes red.
//
// [PublicAPI]: every consumer (DefaultAgentDefinition.Validate, AgentDefinitionCapabilitiesSyncTests)
// reads through `All`/`Set`, never an individual constant by C# symbol — so ReSharper sees each
// named constant (RoleFiles, Hooks, …) as reachable only from this file and suggests narrowing
// them to private (MemberCanBePrivate.Global, confirmed empirically, not assumed). That would
// defeat the point of naming each one: the catalog is deliberately a public, per-capability id
// list mirroring the .ts source, kept public for discoverability and any future direct reference
// — not dead code.
[PublicAPI]
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
