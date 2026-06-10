using System.Text.Json;
using PetBox.Core.Search;
using PetBox.Memory.Contract;
using PetBox.Sessions.Contract;

namespace PetBox.Web.Search;

// The two-stage session search (spec: session-provenance-bridge):
//   1. DISCOVERY — hybrid (lexical ⊕ semantic, RRF) over the always-on session digests
//      (the `session-digests` memory store SessionDigestJob maintains): cheap, no
//      hydration, sublinear to archive size — the K it returns is constant.
//   2. EPISODIC — the top-K candidate sessions are lazily hydrated and searched INSIDE
//      (ISessionEpisodicIndex), each hit carrying the message ordinal: the provenance
//      bridge from found-by-meaning to the verbatim source (session.get).
// Candidates keep their discovery order; a session that vanished under a stale digest
// is skipped, not an error.
public sealed class SessionSearchService
{
	public const int DefaultSessions = 10;
	// The hydration cap per query. Recall saturates by K≈20-30 (eval m-dcbc8d51);
	// hydrations are sequential, so RAM stays bounded by the episodic cache cap.
	public const int MaxSessions = 30;
	public const int DefaultHitsPerSession = 5;
	public const int MaxHitsPerSession = 20;

	readonly IMemoryService _memory;
	readonly ISessionEpisodicIndex _episodic;

	public SessionSearchService(IMemoryService memory, ISessionEpisodicIndex episodic)
	{
		_memory = memory;
		_episodic = episodic;
	}

	public async Task<SessionSearchOutcome> SearchAsync(string projectKey, string query,
		int sessions = 0, int hitsPerSession = 0, CancellationToken ct = default)
	{
		sessions = Math.Clamp(sessions <= 0 ? DefaultSessions : sessions, 1, MaxSessions);
		hitsPerSession = Math.Clamp(hitsPerSession <= 0 ? DefaultHitsPerSession : hitsPerSession, 1, MaxHitsPerSession);

		// No digest store yet = distillation hasn't reached this project; say so instead
		// of failing (the store auto-vivifies on the first distilled session).
		if (!await _memory.StoreExistsAsync(projectKey, SessionDigestJob.Store, ct))
			return new SessionSearchOutcome(false, [], new SearchRetrievers(false, false, false));

		var discovery = await _memory.SearchAsync(projectKey, SessionDigestJob.Store, query, type: null, ct: ct);

		var candidates = new List<SessionSearchCandidate>();
		foreach (var digest in discovery.Hits.Take(sessions))
		{
			ct.ThrowIfCancellationRequested();
			var (sessionId, agent) = Provenance(digest);
			var inner = await _episodic.SearchAsync(projectKey, sessionId, query, hitsPerSession, ct);
			if (inner is null) continue; // session deleted after distillation — stale digest
			candidates.Add(new SessionSearchCandidate(sessionId, agent, digest.Description, inner.Hits, inner.Retrievers));
		}

		return new SessionSearchOutcome(true, candidates, discovery.Retrievers);
	}

	// The digest entry's metadata carries the provenance (sessionId + agent) the
	// distiller stamped; the entry key doubles as the sessionId fallback.
	static (string SessionId, string Agent) Provenance(MemoryEntryView digest)
	{
		if (!string.IsNullOrWhiteSpace(digest.Metadata))
		{
			try
			{
				using var doc = JsonDocument.Parse(digest.Metadata);
				var sessionId = doc.RootElement.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
				var agent = doc.RootElement.TryGetProperty("agent", out var a) ? a.GetString() : null;
				return (sessionId ?? digest.Key, agent ?? "");
			}
			catch (JsonException) { /* fall through to the key */ }
		}
		return (digest.Key, "");
	}
}

// One discovered session: its digest description (what the session is about), the
// episodic hits inside it (message ordinal = the session.get bridge) and the inner
// retriever provenance.
public sealed record SessionSearchCandidate(
	string SessionId,
	string Agent,
	string Description,
	IReadOnlyList<SessionEpisodicHit> Hits,
	SearchRetrievers Retrievers);

// Distilled=false → the project has no digest store yet (background distillation
// hasn't run); an honest "not indexed yet", distinct from "nothing matched".
public sealed record SessionSearchOutcome(
	bool Distilled,
	IReadOnlyList<SessionSearchCandidate> Candidates,
	SearchRetrievers Discovery);
