namespace PetBox.Sessions.Contract;

// REST echo of a session push: the session id, its new version (last message ordinal), and
// how many messages the snapshot now holds. Mirrored by the MCP session.upsert structured output.
public sealed record SessionUpsertResponse(string SessionId, long Version, int MessageCount);
