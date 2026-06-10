using PetBox.Core.Data.Temporal;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;

namespace PetBox.Sessions.Services;

// The one implementation of ISessionService: the single door to the sessions store.
// The temporal upsert (the only write) lives here, so the MCP tool and the REST
// Stop-hook endpoint share one code path instead of each opening the context.
public sealed class SessionService : ISessionService
{
	readonly ISessionStore _sessions;

	public SessionService(ISessionStore sessions) => _sessions = sessions;

	public async Task<SessionUpsertOutcome> UpsertAsync(string projectKey, string sessionId, string agent, string content, long version = 0, CancellationToken ct = default)
	{
		var ctx = _sessions.GetContext(projectKey);
		var r = await TemporalStore.UpsertAsync(ctx, new[]
		{
			new SessionRow { Key = sessionId, Version = version, Agent = agent, Content = content },
		}, ct: ct);
		return new SessionUpsertOutcome(r);
	}

	public Task<SessionRow?> GetAsync(string projectKey, string sessionId, CancellationToken ct = default) =>
		_sessions.GetAsync(projectKey, sessionId, ct);

	public Task<IReadOnlyList<SessionRow>> ListAsync(string projectKey, CancellationToken ct = default) =>
		_sessions.ListAsync(projectKey, ct);
}
