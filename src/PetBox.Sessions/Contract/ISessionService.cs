using PetBox.Core.Data.Temporal;
using PetBox.Sessions.Data;

namespace PetBox.Sessions.Contract;

// The single entry point to the Sessions module for every caller (MCP tools + the
// REST Stop-hook endpoint). Both used to open the per-project sessions context
// directly to write; routing them here keeps the one write path in one place.
// A NetArchTest forbids Web from reaching ISessionStore / SessionsDb directly.
public interface ISessionService
{
	// Optimistic-concurrency temporal upsert of a session's plan blob: replace at the
	// caller-supplied baseline version, conflict on a stale baseline.
	Task<SessionUpsertOutcome> UpsertAsync(string projectKey, string sessionId, string agent, string content, long version = 0, CancellationToken ct = default);
	Task<SessionRow?> GetAsync(string projectKey, string sessionId, CancellationToken ct = default);
	Task<IReadOnlyList<SessionRow>> ListAsync(string projectKey, CancellationToken ct = default);
}

// The raw temporal upsert result, ready for an adapter to serialize.
public sealed record SessionUpsertOutcome(TemporalUpsertResult<SessionRow> Result);
