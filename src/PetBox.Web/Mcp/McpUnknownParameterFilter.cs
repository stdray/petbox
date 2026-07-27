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
// quality memory_upsert.store already gives for an unknown VALUE — NamespaceSuggest is reused
// as-is, not reimplemented, for the edit-distance ranking.
//
// SCOPE IS DELIBERATELY SHALLOW: only the TOP-LEVEL argument keys are checked against the
// TOP-LEVEL schema properties. A batch verb's nested objects (tasks_upsert's `nodes[]`,
// comments_upsert's `entries[]`, apikey_update-style batches) are VALUES living under one
// legitimate top-level key — their own field names are never walked here, so a rename inside an
// array-item shape (a materially harder problem: which nested shape, which array?) cannot be
// mistaken for an unknown top-level parameter and cannot false-positive a batch call.
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

		foreach (var key in p.Arguments!.Keys)
		{
			if (knownSet.Contains(key)) continue;

			var near = NamespaceSuggest.Nearest(key, known);
			var hint = near.Count == 0 ? "" : $" Did you mean {string.Join(" / ", near.Select(n => $"'{n}'"))}?";
			throw new ArgumentException($"Unknown parameter '{key}' for tool '{tool}'.{hint}");
		}
	}
}
