namespace PetBox.Web.Mcp;

// The names drop-legacy-aliases RETIRED, and what replaced each one.
//
// Deleting an alias from the schema makes it unknown; that is enough to stop it being silently
// dropped, but not enough to be USEFUL. "Unknown parameter 'l1'" tells a caller that its spelling is
// wrong and leaves it to guess which of fourteen accepted names is the one it wanted. This table is
// what turns the refusal into an instruction — the alias is named, and so is its successor.
//
// Keyed by TOOL because the same name can be retired in one scope and live in another:
// `fromNodeId` is a retired ITEM field of relations_create AND a legitimate top-level parameter of
// its single form. That is safe here for a structural reason, not a lucky one — this table is
// consulted ONLY for names the schema already rejected in the scope they appeared in, so a valid
// top-level `fromNodeId` never reaches the lookup at all.
//
// This is a MIGRATION AID with a shelf life, not a permanent second contract surface. It carries no
// behaviour: every name in it is already gone from the schema, and the entry only improves the
// sentence. Entries may be dropped once callers have moved.
static class McpRetiredParameters
{
	static readonly Dictionary<string, Dictionary<string, string>> Retired = new(StringComparer.Ordinal)
	{
		// tasks_upsert nodes[]: `l1`/`prevL1` were alternative spellings of `key`/`prevKey`. `l1` is
		// also why `key` could not be marked required until it went — see McpRequiredMemberAttribute.
		["tasks_upsert"] = new(StringComparer.Ordinal)
		{
			["l1"] = "key",
			["prevL1"] = "prevKey",
		},
		// tasks_search: a boolean that aliased the statusKind facet. The replacement is not a rename —
		// `includeClosed:true` meant "every kind", which is now the explicit three-value ask, so the
		// hint names the parameter and the caller reads its description for the values.
		["tasks_search"] = new(StringComparer.Ordinal)
		{
			["includeClosed"] = "statusKind",
		},
		// relations_create items[]: the item shape's own duplicates of `from`/`to`.
		["relations_create"] = new(StringComparer.Ordinal)
		{
			["fromNodeId"] = "from",
			["toNodeId"] = "to",
		},
	};

	// The replacement for a retired name on this tool, or null when the name was never ours.
	public static string? ReplacementFor(string tool, string name) =>
		Retired.TryGetValue(tool, out var forTool) && forTool.TryGetValue(name, out var replacement)
			? replacement
			: null;

	// Every (name, replacement) pair declared for a tool — the drift test reads this to prove each
	// retired name is really absent from the live schema and each replacement really present.
	public static IReadOnlyDictionary<string, string> ForTool(string tool) =>
		Retired.TryGetValue(tool, out var forTool) ? forTool : new Dictionary<string, string>(StringComparer.Ordinal);

	public static IReadOnlyCollection<string> Tools => Retired.Keys;
}
