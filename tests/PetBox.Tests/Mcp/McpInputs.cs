using System.Text.Json;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests;

// Test helper: build the typed MCP tool-input arrays (PlanNodeInput[] / MemoryEntryInputDto[])
// from the anonymous-object literals the tests already use. After typed-surface Phase 4 the
// tasks_upsert / memory_upsert tool methods take typed arrays (so the SDK emits a rich input
// schema) instead of a raw JsonElement; these helpers do the same JSON round-trip the SDK
// would, with case-insensitive matching so `{ key = ... }` literals bind to PascalCase records.
public static class McpInputs
{
	static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

	public static PlanNodeInput[] Nodes(object array) =>
		JsonSerializer.Deserialize<PlanNodeInput[]>(JsonSerializer.Serialize(array), Opts)!;

	// From a raw JSON array string (some tests author the payload as a literal).
	public static PlanNodeInput[] NodesJson(string json) =>
		JsonSerializer.Deserialize<PlanNodeInput[]>(json, Opts)!;

	public static MemoryEntryInputDto[] Entries(object array) =>
		JsonSerializer.Deserialize<MemoryEntryInputDto[]>(JsonSerializer.Serialize(array), Opts)!;

	public static MemoryEntryInputDto[] EntriesJson(string json) =>
		JsonSerializer.Deserialize<MemoryEntryInputDto[]>(json, Opts)!;
}
