using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PetBox.Web.Mcp;

// Card work/unknown-param-silently-ignored-breaks-renames-quietly. LIVE INCIDENT 27.07.2026: right
// after batch 3's param renames shipped (0458cff8: `under`→`underNode` etc., e328c7cf), the
// orchestrator's still-live session called tasks_search with the just-retired `under`. It did not
// error. The ModelContextProtocol tool binder pulls each C# parameter's JsonElement out of the
// arguments dict BY NAME and simply never reads a key that matches none — so the filter silently
// dropped, and the call returned the WHOLE board (60+ nodes) instead of the 11-node subtree. No
// error, no warning, indistinguishable from a correctly-filtered answer.
//
// DECISIVE MEASUREMENT (before writing this file): a raw HTTP tools/call, bypassing every MCP
// client/SDK entirely — {"board":"work","zzz_nonexistent":1,"bodyLen":0} against tasks_search —
// still returned the full unfiltered board with no error. That proves the loss is SERVER-side (the
// framework's per-parameter lookup), not a client snapshot-schema artifact, so it is fixable here.
//
// This filter closes the gap: a tools/call whose TOP-LEVEL argument keys include one absent from
// the tool's own generated input schema `properties` is REJECTED, with the same "did you mean 'X'?"
// quality memory_upsert.store already gives for an unknown VALUE.
//
// The hint text is built by ParamNameSuggest (see its header for the full rationale), NOT
// NamespaceSuggest directly, though NamespaceSuggest.Distance is still the shared Levenshtein
// core. FOUND-DURING-VERIFICATION (do not regress this): an earlier version of this filter called
// NamespaceSuggest.Nearest directly and shipped with a green test — but on the live incident's
// actual renames (`under`->`underNode`, `boadr`->`board`) that produced NO hint at all in
// production. NamespaceSuggest's budget is tuned for its own open-ended namespace domain and is too
// tight for this closed, short parameter-name domain. ParamNameSuggest also always appends the
// tool's accepted-parameter list, because a rename can land nowhere near the old name at all
// (`keys`->`nodes`) — no edit-distance or prefix threshold will ever bridge that, and the caller's
// schema snapshot is stale by construction (this card's whole premise), so it cannot look the
// rename up itself.
//
// SCOPE IS TOP-LEVEL KEYS PLUS ONE LEVEL OF BATCH ITEMS. The original version stopped at the top
// level, on the reasoning that a nested rename is "a materially harder problem: which nested shape,
// which array?". drop-legacy-aliases retired `l1`/`prevL1` (tasks_upsert `nodes[]`) and
// `fromNodeId`/`toNodeId` (relations_create `items[]`) — every one of them an ITEM field, none of
// them reachable by a top-level check. Leaving the walk shallow would have retired those names into
// exactly the silence this filter exists to end: `{key:null, l1:"x"}` would have failed with a
// generic "each node needs a 'key'" that never names the field the caller actually sent, and
// `{from:null, fromNodeId:"x"}` with "from is required" — a stale caller told it forgot a field it
// did supply.
//
// The "which nested shape" question turned out to be answered by the schema itself: a batch param
// is `{"type":["array",…], "items":{"type":"object", "properties":{…}}}`, so the item shape is
// exactly one hop from the param whose value we are holding. The walk therefore goes ONE level and
// no further — an item's own nested objects (tasks_upsert's `links`, an open dictionary of
// relation-kind → ref) have no closed property set to check against and are left alone. The
// descent happens ONLY when the item schema really does carry `properties`; anything else
// fails open, same as the top-level lookup.
//
// COST OF THE HARD EDGE (accepted, and the point): pasting a `tasks_search` row straight back into
// `tasks_upsert.nodes[]` now ERRORS on `nodeId`/`score`/`parentSlug` instead of quietly dropping
// them. That is the correct trade for a WRITE verb — the read row was never the write shape, and a
// caller who believes it is has a bug either way; now it is told.
//
// FAIL-OPEN only around the SCHEMA LOOKUP (no schema found, no `properties`, DI hiccup) — mirrors
// every other filter's stance toward its OWN infra failing. Once a schema with `properties` IS
// found, an unknown key is a hard reject, not a soft one: that hard edge is the entire point (a
// write verb's silently-dropped rename is a lost mutation, e.g. apikey_update's `keyValue`).
static class McpUnknownParameterFilter
{
	public static void Register(IMcpRequestFilterBuilder filters) =>
		filters.AddCallToolFilter(next => (request, ct) =>
		{
			Assert(request);
			return next(request, ct);
		});

	// One offending name: how the caller wrote it (`display`), the leaf to score suggestions against,
	// the accepted vocabulary of the SCOPE it appeared in (top-level names, or an item's fields), and
	// that scope's REQUIRED names — the fields a caller must keep when it strips a payload back down.
	readonly record struct Offender(string Display, string Leaf, List<string> Scope, List<string> Required);

	static void Assert(RequestContext<CallToolRequestParams> request)
	{
		if (request.Params is not { Name: { Length: > 0 } tool, Arguments.Count: > 0 } p) return;

		if (McpProjectDefaultFilter.Schema(request.Services, tool) is not { } schema) return;
		if (schema.ValueKind != JsonValueKind.Object) return;
		if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
			return;

		var known = properties.EnumerateObject().Select(pr => pr.Name).ToList();
		var knownSet = new HashSet<string>(known, StringComparer.Ordinal);

		// COLLECT EVERY offender before refusing, rather than throwing on the first. A read-modify-write
		// paste carries a HANDFUL of response-only fields at once (nodeId + board + score + …); failing
		// on one at a time turns one mistake into one round-trip per field, and an agent doing that has
		// no way to see it is in a loop. Dedup is by display name — the same stray field repeated across
		// twenty batch items is ONE thing to fix, not twenty.
		var offenders = new List<Offender>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var topRequired = RequiredNames(schema);
		void Add(string display, string leaf, List<string> scope, List<string> required)
		{
			if (seen.Add(display)) offenders.Add(new Offender(display, leaf, scope, required));
		}

		foreach (var (key, value) in p.Arguments!)
		{
			if (!knownSet.Contains(key))
			{
				Add(key, key, known, topRequired);
				continue; // an unknown top-level key has no item schema to descend into
			}

			// One hop into a batch param's item shape (see the header). The display name keeps the
			// `nodes[].` prefix so the message stays actionable inside a batch.
			if (ItemSchema(properties, key) is not { } itemSchema) continue;
			if (!itemSchema.TryGetProperty("properties", out var itemProps)) continue;
			var itemKnown = itemProps.EnumerateObject().Select(pr => pr.Name).ToList();
			var itemKnownSet = new HashSet<string>(itemKnown, StringComparer.Ordinal);
			var itemRequired = RequiredNames(itemSchema);
			if (value.ValueKind != JsonValueKind.Array) continue;
			foreach (var item in value.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object) continue;
				foreach (var field in item.EnumerateObject())
					if (!itemKnownSet.Contains(field.Name))
						Add($"{key}[].{field.Name}", field.Name, itemKnown, itemRequired);
			}
		}

		if (offenders.Count > 0) throw Unknown(offenders, tool, request.Services);
	}

	// Build ONE refusal covering every offender, split into the two things a caller can actually
	// have done. The split exists because the two need OPPOSITE advice and the generic sentence
	// serves neither well:
	//
	//   RESPONSE — the name is one the server ITSELF emits in this family's read output. This is the
	//              read-modify-write paste, and it is the case where a bare "Unknown parameter" is
	//              actively misleading: the caller did not invent the field, it read it here. Telling
	//              it the field is unknown invites the conclusion that its own state is corrupt. It
	//              must be told the field is response-only and simply has to be dropped.
	//   UNKNOWN  — a typo, a name from nowhere, or a spelling this surface USED to accept. The
	//              original treatment: nearest-match plus the accepted list.
	//
	// There is deliberately no third branch naming a retired parameter's successor
	// (drop-retired-parameter-hints). A table of former spellings saves a stale caller one round
	// trip and costs a second contract surface that nothing expires: its entries outlive the
	// migration, and every later rename has to remember to feed it. The two branches below already
	// name the offending parameter, say it is not accepted, list what IS accepted in that scope, and
	// point at the nearest live name — which is what a caller needs in order to fix the call, with or
	// without being told the old name's history.
	static ArgumentException Unknown(List<Offender> offenders, string tool, IServiceProvider? services)
	{
		var responseFields = ResponseFieldsOf(services, tool);

		var response = new List<Offender>();
		var unknown = new List<Offender>();
		foreach (var o in offenders)
		{
			if (responseFields.Contains(o.Leaf))
				response.Add(o);
			else
				unknown.Add(o);
		}

		// The opener carries NO names. Each offending name is then printed exactly ONCE, by the branch
		// that has something useful to say about it — repeating the full list up front and again per
		// branch is how the first draft of this read, and on the read-modify-write case (five stray
		// fields) that doubled an already long message for no added information.
		var sb = new System.Text.StringBuilder($"Unknown parameter(s) for tool '{tool}'.");

		if (response.Count > 0)
		{
			// Name the scope's REQUIRED fields (derived from the same schema, so it cannot drift) rather
			// than saying "keep the identity" and leaving the caller to work out which field that is. On
			// tasks_upsert this renders "keep 'key'", which is exactly the instruction a read-modify-write
			// caller needs and cannot get from the generic sentence.
			var keep = response[0].Required;
			var keepText = keep.Count == 0
				? ""
				: $", keeping {string.Join(", ", keep.Select(k => $"'{k}'"))} (that is what identifies the record)";
			sb.Append($" READ-ONLY response field(s): {string.Join(", ", response.Select(o => $"'{o.Display}'"))}" +
				$" — the server emits these when you READ and does not accept them when you WRITE." +
				$" A row you read back is not a write payload: drop them and resend only the fields you are changing{keepText}.");
		}

		if (unknown.Count > 0)
		{
			sb.Append($" Unrecognized: {string.Join(", ", unknown.Select(o => $"'{o.Display}'"))}.");
			// The nearest-match hint is scored against the LEAF so an item field is compared with item
			// fields, not with its `nodes[].` prefix. With a single offender the name was just printed
			// one clause ago, so the hint drops the redundant "'boad':" prefix and reads as it always did.
			var hints = unknown
				.Select(o => (o.Display, Near: ParamNameSuggest.Nearest(o.Leaf, o.Scope)))
				.Where(h => h.Near.Count > 0)
				.ToList();
			if (hints.Count == 1)
				sb.Append($" Did you mean {string.Join(" / ", hints[0].Near.Select(n => $"'{n}'"))}?");
			else if (hints.Count > 1)
				sb.Append($" Did you mean {string.Join("; ", hints.Select(h => $"'{h.Display}': {string.Join(" / ", h.Near.Select(n => $"'{n}'"))}"))}?");
		}

		// The accepted vocabulary of each scope that had an offender — always, for every branch: a
		// caller's cached schema is stale by construction (this filter's whole premise), so it cannot
		// look the answer up itself.
		foreach (var scope in offenders.Select(o => o.Scope).Distinct())
			sb.Append($" Accepted parameters: {ParamNameSuggest.Describe(scope)}.");

		return new ArgumentException(sb.ToString());
	}

	// Every property name appearing ANYWHERE in the OUTPUT schemas of this tool's family (`tasks_*`
	// for tasks_upsert, `relations_*` for relations_create, …) — i.e. the vocabulary the server hands
	// a caller when it reads.
	//
	// DERIVED, NOT CURATED, and that is the point. A hand-written list of "fields we emit" is a
	// second copy of the wire contract, and the copy rots the first time a view type gains a field —
	// at which point the message silently degrades back to the unhelpful branch for exactly the new
	// field nobody has learned yet. Reading the live schemas means the classification tracks the
	// output shapes by construction.
	//
	// FAMILY-SCOPED rather than server-wide so the claim in the message stays literally true: the
	// fields named really are ones THIS tool's own read verbs return, not a coincidence with some
	// unrelated verb's output. Over-breadth is bounded anyway — a name only reaches this lookup after
	// the input schema has already rejected it, so a legitimate input field can never be classified
	// as response-only no matter how wide this set gets.
	//
	// Cached per ToolCollection instance: the collection is a DI singleton built once at startup, so
	// the walk happens once per family and never on the hot path.
	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, HashSet<string>>> FamilyFields = new();

	static HashSet<string> ResponseFieldsOf(IServiceProvider? services, string tool)
	{
		var collection = services?.GetService<IOptions<McpServerOptions>>()?.Value.ToolCollection;
		if (collection is null) return [];

		var byFamily = FamilyFields.GetValue(collection, static _ => new Dictionary<string, HashSet<string>>(StringComparer.Ordinal));
		var family = tool.IndexOf('_') is var i and > 0 ? tool[..i] : tool;
		lock (byFamily)
		{
			if (byFamily.TryGetValue(family, out var cached)) return cached;

			var fields = new HashSet<string>(StringComparer.Ordinal);
			foreach (var t in collection)
			{
				if (t is not McpServerTool st) continue;
				if (!st.ProtocolTool.Name.StartsWith(family + "_", StringComparison.Ordinal)) continue;
				if (st.ProtocolTool.OutputSchema is { } output) CollectPropertyNames(output, fields, depth: 0);
			}
			byFamily[family] = fields;
			return fields;
		}
	}

	// Walk an output schema and collect every `properties` key at any nesting depth. The depth cap is
	// a cheap guard against a pathological/self-referential schema, not a semantic limit — real output
	// shapes here nest three or four levels (result -> nodes[] -> links[] -> …).
	static void CollectPropertyNames(JsonElement schema, HashSet<string> into, int depth)
	{
		if (depth > 8 || schema.ValueKind != JsonValueKind.Object) return;
		if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
			foreach (var prop in props.EnumerateObject())
			{
				into.Add(prop.Name);
				CollectPropertyNames(prop.Value, into, depth + 1);
			}
		if (schema.TryGetProperty("items", out var items))
			CollectPropertyNames(items, into, depth + 1);
		// `["T","null"]` unions and $defs indirection both surface as sibling subschemas.
		foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
			if (schema.TryGetProperty(keyword, out var branches) && branches.ValueKind == JsonValueKind.Array)
				foreach (var branch in branches.EnumerateArray())
					CollectPropertyNames(branch, into, depth + 1);
		if (schema.TryGetProperty("$defs", out var defs) && defs.ValueKind == JsonValueKind.Object)
			foreach (var def in defs.EnumerateObject())
				CollectPropertyNames(def.Value, into, depth + 1);
	}

	// The item OBJECT SCHEMA for a batch parameter — `{"type":["array",…],"items":{"type":"object",
	// "properties":{…}}}` — or null for anything that is not a closed array-of-objects (fail open,
	// exactly like the top-level schema lookup). The whole item schema is returned, not just its
	// `properties`, because the caller also needs its `required` to say which fields to keep.
	static JsonElement? ItemSchema(JsonElement schemaProperties, string param)
	{
		if (!schemaProperties.TryGetProperty(param, out var paramSchema)) return null;
		if (paramSchema.ValueKind != JsonValueKind.Object) return null;
		if (!paramSchema.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
			return null;
		if (!items.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
			return null;
		return items;
	}

	// An object schema's `required` names (empty when it declares none).
	static List<string> RequiredNames(JsonElement objectSchema) =>
		objectSchema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
			? required.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
				.Select(e => e.GetString()!).ToList()
			: [];
}
