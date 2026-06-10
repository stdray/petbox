namespace PetBox.Sessions.Contract;

// REST echo of a session blob write: whether the temporal upsert applied and the
// store version after it. Mirrored by the MCP session tool's structured output.
public sealed record SessionUpsertResponse(bool Applied, long CurrentVersion);
