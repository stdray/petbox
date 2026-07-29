namespace PetBox.Web.Mcp;

// The names drop-legacy-aliases RETIRED, and what replaced each one.
//
// Deleting an alias from the schema makes it unknown; that is enough to stop it being silently
// dropped, but not enough to be USEFUL. "Unknown parameter 'l1'" tells a caller that its spelling is
// wrong and leaves it to guess which of fourteen accepted names is the one it wanted. This table is
// what turns the refusal into an instruction — the alias is named, and so is its successor.
//
// Keyed by TOOL. The lookup is consulted ONLY for names the schema already rejected in the scope
// they appeared in, so an entry here can never shadow a name that is still live somewhere: a valid
// parameter never reaches this table at all. (`fromNodeId` relied on that while it was retired as a
// relations_create ITEM field but still live as its single-form parameter — the single form has
// since been renamed to `from`/`to` too, so the name is now retired in BOTH scopes and one entry
// covers both.)
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
		// relations_create: the item shape's duplicates of `from`/`to`, AND the single form's own
		// parameters, which were renamed to the same two names (uniform-node-ref-naming: a node
		// reference resolves a slug OR a NodeId, so its name must not end in NodeId and promise half).
		["relations_create"] = new(StringComparer.Ordinal)
		{
			["fromNodeId"] = "from",
			["toNodeId"] = "to",
		},
		// db_create / db_delete: the bare `name` was the odd one out — db_describe and every data_*
		// tool already called this same concept `dbName`. One concept, one qualified name across the
		// db_*/data_* family (mcp-surface-naming-cleanup wave 2).
		["db_create"] = new(StringComparer.Ordinal)
		{
			["name"] = "dbName",
		},
		["db_delete"] = new(StringComparer.Ordinal)
		{
			["name"] = "dbName",
		},
		// log_create / log_update / log_delete: `name` here numerically outnumbered `logName`
		// (log_query's own parameter), but headcount is not the tiebreaker — one concept must carry
		// one name across the family, and `logName` is the one already load-bearing on the read side.
		["log_create"] = new(StringComparer.Ordinal)
		{
			["name"] = "logName",
		},
		["log_update"] = new(StringComparer.Ordinal)
		{
			["name"] = "logName",
		},
		["log_delete"] = new(StringComparer.Ordinal)
		{
			["name"] = "logName",
		},
		// data_schema_apply: the bare `name` here was never the DataDb's name (that's `dbName`,
		// right next to it) — it is the migration's journal key, a different concept that happened to
		// share the db family's most generic word. Renaming it to `migrationName` stops it reading as
		// a second, competing spelling of `dbName` in the same parameter list.
		["data_schema_apply"] = new(StringComparer.Ordinal)
		{
			["name"] = "migrationName",
		},
		// tasks_methodology_* INSTANCE verbs: the instance's slug ADDRESS was called `name` while the
		// TEMPLATE half of the very same family already called the identical concept `key`
		// (template_upsert/_get/_delete/_snapshot). Worse than an inconsistency: `name` was doing two
		// jobs at once here — the slug you address a resource by, and the human display prose of a
		// document (template_list returns BOTH a `key` and a `name`; a definition's `name` is
		// ordinary prose). One word, two concepts, inside one family. After this wave `key` is the
		// slug address everywhere in tasks_methodology_* and `name` is display prose only — and the
		// same rename landed on the RESPONSE side in the same wave, so a slug read out of `key` goes
		// straight back into `key` (mcp-surface-naming-cleanup wave 5).
		["tasks_methodology_create"] = new(StringComparer.Ordinal)
		{
			["name"] = "key",
		},
		["tasks_methodology_get"] = new(StringComparer.Ordinal)
		{
			["name"] = "key",
		},
		["tasks_methodology_close"] = new(StringComparer.Ordinal)
		{
			["name"] = "key",
		},
		["tasks_methodology_set_active"] = new(StringComparer.Ordinal)
		{
			["name"] = "key",
		},
		["tasks_methodology_rules_get"] = new(StringComparer.Ordinal)
		{
			["name"] = "key",
		},
		["tasks_methodology_rules_upsert"] = new(StringComparer.Ordinal)
		{
			["name"] = "key",
		},
		["tasks_methodology_set_description"] = new(StringComparer.Ordinal)
		{
			["name"] = "key",
		},
		// The guide's `name` was OPTIONAL, which makes its retirement the one that most needed this
		// entry: a stale caller passing `name:"quartet"` would not have been told it had asked for
		// nothing — it would have got the active-pointer guide for a DIFFERENT instance and had no
		// way to notice.
		["tasks_methodology_guide"] = new(StringComparer.Ordinal)
		{
			["name"] = "key",
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
