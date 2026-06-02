using PetBox.Core.Data.Temporal;
using PetBox.Sessions.Data;

namespace PetBox.Sessions.Contract;

// The single entry point to the Sessions module for every caller (MCP tools + the
// REST Stop-hook endpoint). Both used to open the per-project sessions context
// directly to append; routing them here keeps the one write path in one place.
// A NetArchTest forbids Web from reaching ISessionStore / SessionsDb directly.
public interface ISessionService
{
	// Create/update a session's plan blob with the caller-supplied baseline version.
	Task<SessionUpsertOutcome> AppendAsync(string projectKey, string sessionId, string agent, string content, long version = 0, CancellationToken ct = default);
	Task<SessionRow?> GetAsync(string projectKey, string sessionId, CancellationToken ct = default);
	Task<IReadOnlyList<SessionRow>> ListAsync(string projectKey, CancellationToken ct = default);
}

// The raw temporal upsert result, ready for an adapter to serialize.
public sealed record SessionUpsertOutcome(TemporalUpsertResult<SessionRow> Result);
