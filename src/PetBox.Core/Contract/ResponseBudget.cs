using System.Text.Json;
using System.Text.Json.Serialization;
using PetBox.Core.Json;

namespace PetBox.Core.Contract;

// One response-wide char budget for list-shaped tool results (spec surface-economy /
// bounded-result-sets): a read built for agents must stay inside a context window no
// matter how large the store grows — and never truncate silently. Row costs are measured
// on the WIRE form of each row (camelCase JSON, null fields omitted, Cyrillic-safe encoder),
// rows are prefix-cut at the first one that no longer fits, and the caller marks the cut
// structurally (truncated:true + omitted:<n> + a narrowing hint).
// One instance = one response: spending accumulates across Take calls, so several lists
// (e.g. the four methodology boards) share a single budget in emission order. Shared by
// tasks_search / tasks_methodology_get / memory_search / session_search / comments_search.
public sealed class ResponseBudget
{
	// Default budget: ~30k serialized chars keeps a tool result well inside an agent's
	// context window while leaving room for the response envelope.
	public const int DefaultChars = 30_000;

	// NOT PetBoxJsonEncoder.SharedOptions wholesale — tried that first and it broke two tests
	// (TasksGetBudgetTests + MemoryRowWeightTests) by measuring 12-100+ chars/row too HIGH, on
	// pure-ASCII fixtures where the encoder can't be the cause. Root cause: SharedOptions is
	// `JsonSerializerDefaults.Web` + Relaxed encoder ONLY — no DefaultIgnoreCondition — because
	// its OTHER call sites don't need null-omission. The real MCP wire (Program.cs's mcpJson =
	// McpJsonUtilities.DefaultOptions + Relaxed) DOES omit nulls (that comes from
	// McpJsonUtilities.DefaultOptions itself, confirmed by inspecting it directly). Adopting
	// SharedOptions as-is would silently stop omitting null row fields (Score, Truncated,
	// Omitted, Hint, retriever metadata, …), overcounting every row and firing Take()'s cut too
	// early — trading one silent miscount for another. This is exactly the case
	// PetBoxJsonEncoder.SharedOptions's own doc comment calls out: "A call site that
	// legitimately needs its own JsonSerializerOptions ... should still set Encoder =
	// PetBoxJsonEncoder.Relaxed on ITS OWN options rather than adopt this instance wholesale."
	// So: own options, but the ENCODER is the single shared instance (PetBoxJsonEncoder
	// .Relaxed) — the one piece that actually drifted (this class had none, defaulting to the
	// escaping HTML-safe encoder, re-escaping every Cyrillic char to \uXXXX — 6 chars instead
	// of 1 — inflating measured cost ~1.68x on Cyrillic-heavy rows and truncating tasks_search /
	// tasks_methodology_get / memory_search / session_search / comments_search earlier than the
	// real wire size warranted). PetBoxJsonEncoder lives in PetBox.Core (same project as this
	// class), so sharing it costs no new project dependency — Core never reaches into Web or
	// the MCP SDK for it.
	// internal (not private): JsonOptionsWiringTests pins Encoder to PetBoxJsonEncoder.Relaxed
	// BY REFERENCE, so a future "just inline my own JsonSerializerOptions with no Encoder" edit
	// fails the build instead of silently re-diverging a sixth time.
	internal static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Encoder = PetBoxJsonEncoder.Relaxed,
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
