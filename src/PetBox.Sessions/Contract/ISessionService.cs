namespace PetBox.Sessions.Contract;

// The single entry point to the Sessions module for every caller (the MCP tools + the REST
// Stop-hook endpoint). Both used to open the per-project sessions context directly to write;
// routing them here keeps the one write path in one place. NetArchTests forbid PetBox.Web.Mcp
// and PetBox.Web.Sessions from reaching ISessionStore / SessionsDb directly.
public interface ISessionService
{
	// Latest-snapshot write, last-write-wins: replace the session's content with these messages.
	// The server numbers them (ordinal 1..N); the returned Version is the last message's ordinal.
	// Kept as the full-snapshot PUT for repair/import; the incremental path is AppendAsync.
	Task<SessionUpsertOutcome> UpsertAsync(string projectKey, string sessionId, string agent, IReadOnlyList<SessionMessageInput> messages, CancellationToken ct = default);

	// Server-authoritative incremental write (spec session-append-wire). The client claims its
	// batch starts at ordinal `fromOrdinal`; the server compares against the snapshot's message
	// count (`lastOrdinal`, 0 for a missing session):
	//   - fromOrdinal == lastOrdinal+1 → contiguous: every message is appended;
	//   - fromOrdinal <= lastOrdinal   → overlap: ordinals the server already holds are ignored
	//     IDEMPOTENTLY (existing messages win, nothing is rewritten), the tail is appended;
	//   - fromOrdinal >  lastOrdinal+1 → gap: nothing is written, Applied=false, and LastOrdinal
	//     tells the client where to resend from (LastOrdinal+1).
	// Storage stays latest-snapshot (the blob is re-encoded); a full-overlap call writes nothing.
	Task<SessionAppendOutcome> AppendAsync(string projectKey, string sessionId, string agent, long fromOrdinal, IReadOnlyList<SessionMessageInput> messages, CancellationToken ct = default);

	Task<SessionSnapshot?> GetAsync(string projectKey, string sessionId, CancellationToken ct = default);
	Task<IReadOnlyList<SessionHeader>> ListAsync(string projectKey, CancellationToken ct = default);

	// Resolve a possibly-shortened session id (a unique PREFIX of a full id) to the stored full
	// id, so a human/agent can address a session by its first few chars — the short form that
	// digests and session_search snippets use — instead of pasting the whole UUID. Read/delete
	// convenience only; the write path (Upsert/Append) keeps addressing by the exact id it is
	// given (a prefix there would silently create a new session). See SessionIdResolution.
	Task<SessionIdResolution> ResolveIdAsync(string projectKey, string idOrPrefix, CancellationToken ct = default);

	// Messages with Version greater than the cursor — the incremental delta a Class-B index
	// consumes without the store retaining any history (the snapshot is cumulative). Empty if none.
	Task<IReadOnlyList<SessionMessage>> DeltaAsync(string projectKey, string sessionId, long sinceVersion, CancellationToken ct = default);

	// Soft delete: the row stays but disappears from every read; a re-push of the same
	// SessionId resurrects it. False when the session is missing or already deleted.
	Task<bool> DeleteAsync(string projectKey, string sessionId, CancellationToken ct = default);
}

// The outcome of a session write: the id, its new version (last message ordinal), the message
// count, and the write time. Compact form replacing the old temporal upsert result.
public sealed record SessionUpsertOutcome(string SessionId, long Version, int MessageCount, DateTime Updated);

// The outcome of an incremental append. Applied=false means a contiguity gap — nothing was
// written and LastOrdinal is the server's cursor (resend from LastOrdinal+1). Applied=true
// carries the new cursor; Appended counts the messages actually written (0 = full overlap,
// the idempotent no-op).
public sealed record SessionAppendOutcome(string SessionId, bool Applied, long LastOrdinal, int Appended);

// The outcome of resolving a (possibly shortened) session id against the active sessions:
//   - Match set, Ambiguous empty  → exactly one session matched (an EXACT id always wins, even
//                                    when it is also a prefix of a longer id);
//   - Match null,  Ambiguous []   → no active session matched (a miss);
//   - Match null,  Ambiguous [..] → the prefix collided with 2+ sessions (capped list of the
//                                    colliding full ids) — the caller reports it and asks for
//                                    more characters rather than guessing.
public sealed record SessionIdResolution(string? Match, IReadOnlyList<string> Ambiguous)
{
	public static readonly SessionIdResolution None = new(null, Array.Empty<string>());
	public static SessionIdResolution One(string id) => new(id, Array.Empty<string>());
}
