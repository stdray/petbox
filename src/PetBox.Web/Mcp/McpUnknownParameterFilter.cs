using System.Text.Json;
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

	static void Assert(RequestContext<CallToolRequestParams> request)
	{
		if (request.Params is not { Name: { Length: > 0 } tool, Arguments.Count: > 0 } p) return;

		if (McpProjectDefaultFilter.Schema(request.Services, tool) is not { } schema) return;
		if (schema.ValueKind != JsonValueKind.Object) return;
		if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
			return;

		var known = properties.EnumerateObject().Select(pr => pr.Name).ToList();
		var knownSet = new HashSet<string>(known, StringComparer.Ordinal);

		foreach (var (key, value) in p.Arguments!)
		{
			if (!knownSet.Contains(key))
				throw Unknown(key, tool, known);

			// One hop into a batch param's item shape (see the header). `where` names the offending
			// field the way the caller wrote it, so the message stays actionable inside a batch.
			if (ItemProperties(properties, key) is not { } itemProps) continue;
			var itemKnown = itemProps.EnumerateObject().Select(pr => pr.Name).ToList();
			var itemKnownSet = new HashSet<string>(itemKnown, StringComparer.Ordinal);
			if (value.ValueKind != JsonValueKind.Array) continue;
			foreach (var item in value.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object) continue;
				foreach (var field in item.EnumerateObject())
					if (!itemKnownSet.Contains(field.Name))
						throw Unknown($"{key}[].{field.Name}", tool, itemKnown);
			}
		}
	}

	static ArgumentException Unknown(string key, string tool, List<string> known)
	{
		// Suggest against the LEAF name so an item field is compared with item fields, not with the
		// `nodes[].` prefix the message carries for the caller's benefit.
		var leaf = key[(key.LastIndexOf('.') + 1)..];
		var near = ParamNameSuggest.Nearest(leaf, known);
		var hint = near.Count == 0 ? "" : $" Did you mean {string.Join(" / ", near.Select(n => $"'{n}'"))}?";
		var accepted = ParamNameSuggest.Describe(known);
		return new ArgumentException(
			$"Unknown parameter '{key}' for tool '{tool}'.{hint} Accepted parameters: {accepted}.");
	}

	// The item object's `properties` for a batch parameter — `{"type":["array",…],"items":{"type":
	// "object","properties":{…}}}` — or null for anything that is not a closed array-of-objects
	// (fail open, exactly like the top-level schema lookup).
	static JsonElement? ItemProperties(JsonElement schemaProperties, string param)
	{
		if (!schemaProperties.TryGetProperty(param, out var paramSchema)) return null;
		if (paramSchema.ValueKind != JsonValueKind.Object) return null;
		if (!paramSchema.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
			return null;
		if (!items.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
			return null;
		return props;
	}
}
