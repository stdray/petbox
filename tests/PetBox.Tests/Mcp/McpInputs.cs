using System.Text.Json;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests;

// Test helper: build the typed MCP tool-input arrays (TaskNodeInput[] / MemoryEntryInputDto[])
// from the anonymous-object literals the tests already use. After typed-surface Phase 4 the
// tasks_upsert / memory_upsert tool methods take typed arrays (so the SDK emits a rich input
// schema) instead of a raw JsonElement; these helpers do the same JSON round-trip the SDK
// would, with case-insensitive matching so `{ key = ... }` literals bind to PascalCase records.
public static class McpInputs
{
	static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

	public static TaskNodeInput[] Nodes(object array) =>
		JsonSerializer.Deserialize<TaskNodeInput[]>(JsonSerializer.Serialize(array), Opts)!;

	// From a raw JSON array string (some tests author the payload as a literal).
	public static TaskNodeInput[] NodesJson(string json) =>
		JsonSerializer.Deserialize<TaskNodeInput[]>(json, Opts)!;

	public static MemoryEntryInputDto[] Entries(object array) =>
		JsonSerializer.Deserialize<MemoryEntryInputDto[]>(JsonSerializer.Serialize(array), Opts)!;

	public static MemoryEntryInputDto[] EntriesJson(string json) =>
		JsonSerializer.Deserialize<MemoryEntryInputDto[]>(json, Opts)!;
}

// The statusKind facet sets a test asks for by name. `All` is what the retired `includeClosed:true`
// used to mean (drop-legacy-aliases): every kind, now stated as the explicit three-value ask instead
// of a boolean that widened by omitting the facet. Note the ECHO difference this makes —
// effectiveStatusKind comes back as the resolved three-element set, not the old `null` NEUTRAL.
public static class TestFacets
{
	public static string[] All => ["open", "terminalok", "terminalcancel"];
}
