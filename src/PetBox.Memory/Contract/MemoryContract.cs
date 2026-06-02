using PetBox.Core.Data.Temporal;
using PetBox.Memory.Data;

namespace PetBox.Memory.Contract;

// Public request/response shapes for the Memory service. Adapters (MCP/UI) parse
// input into these and serialize the results; the service owns taxonomy parsing,
// tag normalization, FTS search and the temporal write path.

// One entry as submitted to UpsertAsync. Type/Tags are raw strings — the service
// validates the taxonomy and normalizes the CSV tags (domain rules, one place).
public sealed record MemoryEntryInput
{
	public required string Key { get; init; }
	public long Version { get; init; }
	public required string Type { get; init; }
	public string? Description { get; init; }
	public string? Body { get; init; }
	public string? Tags { get; init; }
	public string? PrevKey { get; init; }
}

// A soft-delete request: close the active entry at Key (Version 0 = regardless).
public sealed record MemoryDelete(string Key, long Version);

// An active entry projected for read surfaces (Type stringified).
public sealed record MemoryEntryView(string Key, string Type, string Description, string Body, string Tags, long Version);

// The raw temporal upsert/delta result, ready for an adapter to serialize.
public sealed record MemoryUpsertOutcome(TemporalUpsertResult<MemoryEntry> Result);
