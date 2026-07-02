using System.Text.Json;
using System.Text.Json.Serialization;

namespace PetBox.Core.Contract;

// One response-wide char budget for list-shaped tool results (spec surface-economy /
// bounded-result-sets): a read built for agents must stay inside a context window no
// matter how large the store grows — and never truncate silently. Row costs are measured
// on the WIRE form of each row (camelCase JSON, null fields omitted — mirroring the MCP
// serializer defaults), rows are prefix-cut at the first one that no longer fits, and the
// caller marks the cut structurally (truncated:true + omitted:<n> + a narrowing hint).
// One instance = one response: spending accumulates across Take calls, so several lists
// (e.g. the four methodology boards) share a single budget in emission order. Shared by
// tasks.get / tasks.methodology_get; session/memory/comments lists are next.
public sealed class ResponseBudget
{
	// Default budget: ~30k serialized chars keeps a tool result well inside an agent's
	// context window while leaving room for the response envelope.
	public const int DefaultChars = 30_000;

	// Approximates the MCP tool-result serialization (McpJsonUtilities defaults:
	// camelCase + null-ignore) so the budget tracks what the caller actually receives.
	static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	readonly int _budget;
	int _spent;

	public ResponseBudget(int budget = DefaultChars) => _budget = budget;

	// Serialized wire cost (chars) of one row.
	public static int CostOf<T>(T row) => JsonSerializer.Serialize(row, WireJson).Length;

	// Prefix-cut `rows` against the remaining budget: rows are kept in order until the
	// first that no longer fits; it and everything after it count as omitted (0 = the
	// complete list fit). Never silent — the caller surfaces Omitted on the response.
	public (IReadOnlyList<T> Rows, int Omitted) Take<T>(IReadOnlyList<T> rows)
	{
		var kept = new List<T>(rows.Count);
		for (var i = 0; i < rows.Count; i++)
		{
			var cost = CostOf(rows[i]);
			if (_spent + cost > _budget)
				return (kept, rows.Count - i);
			_spent += cost;
			kept.Add(rows[i]);
		}
		return (kept, 0);
	}
}
