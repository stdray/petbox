using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;

namespace PetBox.Sessions.Services;

// The one implementation of ISessionService: the single door to the sessions store.
// The write (the only mutation) lives here, so the MCP tool and the REST Stop-hook share
// one code path. Latest-snapshot, last-write-wins — no SCD-2, no optimistic concurrency.
public sealed class SessionService : ISessionService
{
	readonly ISessionStore _sessions;

	public SessionService(ISessionStore sessions) => _sessions = sessions;

	public async Task<SessionUpsertOutcome> UpsertAsync(string projectKey, string sessionId, string agent, IReadOnlyList<SessionMessageInput> messages, CancellationToken ct = default)
	{
		// Server-assigned ordinals (1..N) over the cumulative transcript; the session's version
		// is the last message's ordinal. The hook re-pushes the full transcript each turn, so the
		// stored snapshot is always a superset and the row is simply replaced.
		var numbered = new List<SessionMessage>(messages.Count);
		for (var i = 0; i < messages.Count; i++)
			numbered.Add(new SessionMessage(i + 1, messages[i].Role, messages[i].Content));

		var updated = DateTime.UtcNow;
		var row = new SessionRow
		{
			SessionId = sessionId,
			Agent = agent,
			ContentZ = SessionContent.Encode(numbered),
			Version = numbered.Count,
			Updated = updated,
		};
		await _sessions.UpsertAsync(projectKey, row, ct);
		return new SessionUpsertOutcome(sessionId, row.Version, numbered.Count, updated);
	}

	public async Task<SessionAppendOutcome> AppendAsync(string projectKey, string sessionId, string agent, long fromOrdinal, IReadOnlyList<SessionMessageInput> messages, CancellationToken ct = default)
	{
		if (fromOrdinal < 1)
			throw new ArgumentOutOfRangeException(nameof(fromOrdinal), fromOrdinal, "fromOrdinal must be >= 1");

		// The server-authoritative cursor is the snapshot's message count (ordinals are dense
		// 1..N by construction; a missing/soft-deleted session reads as 0 — an append with
		// fromOrdinal=1 creates/resurrects it, mirroring the upsert semantics).
		var snap = await _sessions.GetAsync(projectKey, sessionId, ct);
		var existing = snap?.Messages ?? Array.Empty<SessionMessage>();
		long lastOrdinal = existing.Count;

		// Gap: the batch does not connect to the snapshot. Nothing is written; the outcome
		// carries the cursor so the client can resend the missing tail from lastOrdinal+1.
		if (fromOrdinal > lastOrdinal + 1)
			return new SessionAppendOutcome(sessionId, Applied: false, lastOrdinal, Appended: 0);

		// Overlap: ordinals the server already holds are skipped idempotently — the stored
		// message wins, a re-send can never duplicate or rewrite history.
		var skip = (int)(lastOrdinal - fromOrdinal + 1);
		if (skip >= messages.Count)
			return new SessionAppendOutcome(sessionId, Applied: true, lastOrdinal, Appended: 0);

		var combined = new List<SessionMessage>(existing.Count + messages.Count - skip);
		combined.AddRange(existing);
		var ordinal = lastOrdinal;
		for (var i = skip; i < messages.Count; i++)
			combined.Add(new SessionMessage(++ordinal, messages[i].Role, messages[i].Content));

		var row = new SessionRow
		{
			SessionId = sessionId,
			Agent = agent,
			ContentZ = SessionContent.Encode(combined),
			Version = ordinal,
			Updated = DateTime.UtcNow,
		};
		await _sessions.UpsertAsync(projectKey, row, ct);
		return new SessionAppendOutcome(sessionId, Applied: true, ordinal, Appended: messages.Count - skip);
	}

	public Task<SessionSnapshot?> GetAsync(string projectKey, string sessionId, CancellationToken ct = default) =>
		_sessions.GetAsync(projectKey, sessionId, ct);

	public Task<IReadOnlyList<SessionHeader>> ListAsync(string projectKey, CancellationToken ct = default) =>
		_sessions.ListAsync(projectKey, ct);

	// Prefix resolution is a pure read — delegate straight to the store. The write path
	// (Upsert/Append) deliberately does NOT call this: it addresses by the exact id it's given.
	public Task<SessionIdResolution> ResolveIdAsync(string projectKey, string idOrPrefix, CancellationToken ct = default) =>
		_sessions.ResolveIdAsync(projectKey, idOrPrefix, ct);

	public async Task<IReadOnlyList<SessionMessage>> DeltaAsync(string projectKey, string sessionId, long sinceVersion, CancellationToken ct = default)
	{
		var snap = await _sessions.GetAsync(projectKey, sessionId, ct);
		if (snap is null) return Array.Empty<SessionMessage>();
		return snap.Messages.Where(m => m.Version > sinceVersion).ToList();
	}

	public Task<bool> DeleteAsync(string projectKey, string sessionId, CancellationToken ct = default) =>
		_sessions.DeleteAsync(projectKey, sessionId, ct);
}
