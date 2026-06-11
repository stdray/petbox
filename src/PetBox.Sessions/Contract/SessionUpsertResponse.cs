namespace PetBox.Sessions.Contract;

// REST echo of a session push: the session id, its new version (last message ordinal), and
// how many messages the snapshot now holds. Mirrored by the MCP session.upsert structured output.
public sealed record SessionUpsertResponse(string SessionId, long Version, int MessageCount);

// REST list of session headers (no bodies): what the history importer's upgrade-only
// guard reads to compare local message counts against server versions.
public sealed record SessionHeaderResponse(string SessionId, string Agent, long Version, DateTime Updated);

public sealed record SessionListResponse(IReadOnlyList<SessionHeaderResponse> Sessions);
