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

	public Task<SessionSnapshot?> GetAsync(string projectKey, string sessionId, CancellationToken ct = default) =>
		_sessions.GetAsync(projectKey, sessionId, ct);

	public Task<IReadOnlyList<SessionHeader>> ListAsync(string projectKey, CancellationToken ct = default) =>
		_sessions.ListAsync(projectKey, ct);

	public async Task<IReadOnlyList<SessionMessage>> DeltaAsync(string projectKey, string sessionId, long sinceVersion, CancellationToken ct = default)
	{
		var snap = await _sessions.GetAsync(projectKey, sessionId, ct);
		if (snap is null) return Array.Empty<SessionMessage>();
		return snap.Messages.Where(m => m.Version > sinceVersion).ToList();
	}
}
