namespace PetBox.Sessions.Contract;

// One conversation message in a session transcript. Version is the server-assigned
// ordinal (1-based) within the cumulative transcript; a session's version == the
// version of its last message (a monotonic content cursor, not optimistic concurrency).
public sealed record SessionMessage(long Version, string Role, string Content);

// An inbound message (role + content) before the server numbers it. The Stop-hook (ndjson)
// and the MCP tool send these in order; the server assigns Version = ordinal on ingest.
public sealed record SessionMessageInput(string Role, string Content);
