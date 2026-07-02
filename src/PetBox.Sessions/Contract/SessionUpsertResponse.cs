namespace PetBox.Sessions.Contract;

// REST echo of a session push: the session id, its new version (last message ordinal), and
// how many messages the snapshot now holds. Mirrored by the MCP session.upsert structured output.
public sealed record SessionUpsertResponse(string SessionId, long Version, int MessageCount);

// REST echo of an incremental append (200): the server-authoritative cursor after the write
// and how many messages were actually appended (0 = full overlap, the idempotent no-op).
public sealed record SessionAppendResponse(string SessionId, long LastOrdinal, int Appended);

// REST body of a contiguity-gap reject (409): structured, not opaque — `lastOrdinal` is the
// server's cursor so the client can self-heal by resending the tail from lastOrdinal+1.
public sealed record SessionAppendGapResponse(string Error, long LastOrdinal);

// REST list of session headers (no bodies): what the history importer's upgrade-only
// guard reads to compare local message counts against server versions.
public sealed record SessionHeaderResponse(string SessionId, string Agent, long Version, DateTime Updated);

public sealed record SessionListResponse(IReadOnlyList<SessionHeaderResponse> Sessions);
