using Microsoft.Extensions.Logging;
using PetBox.Sessions.Contract;

namespace PetBox.Sessions.Search;

// Default ISessionFullScanIndex: iterates every session header (most-recently-updated
// first), hydrates each (ISessionService.GetAsync — the same full-snapshot read
// session_get uses) and checks the query as a raw case-insensitive substring against the
// rendered content. No tokenization, no stemming — a straight Contains, which is exactly
// what the term-FTS leg cannot do (it only prefix-matches whole tokens).
public sealed class SessionFullScanIndex : ISessionFullScanIndex
{
	readonly ISessionService _sessions;
	readonly SessionFullScanOptions _options;
	readonly ILogger<SessionFullScanIndex>? _logger;

	public SessionFullScanIndex(ISessionService sessions, SessionFullScanOptions? options = null,
		ILogger<SessionFullScanIndex>? logger = null)
	{
		_sessions = sessions;
		_options = options ?? new SessionFullScanOptions();
		_logger = logger;
	}

	public async Task<SessionFullScanResult> ScanAsync(string projectKey, string query, CancellationToken ct = default)
	{
		var term = (query ?? string.Empty).Trim();
		if (term.Length == 0) return new SessionFullScanResult([], 0, false);

		var headers = (await _sessions.ListAsync(projectKey, ct))
			.OrderByDescending(h => h.Updated)
			.ToList();

		var capped = headers.Count > _options.MaxSessions;
		var toScan = capped ? headers.Take(_options.MaxSessions).ToList() : headers;
		if (capped)
			_logger?.LogWarning(
				"session full-scan capped for {Project}: {Total} sessions found, scanning only the {Cap} most recently updated",
				projectKey, headers.Count, _options.MaxSessions);

		var hits = new List<string>();
		foreach (var header in toScan)
		{
			ct.ThrowIfCancellationRequested();
			var snap = await _sessions.GetAsync(projectKey, header.SessionId, ct);
			if (snap is null) continue; // deleted between the header list and here
			if (snap.Content.Contains(term, StringComparison.OrdinalIgnoreCase))
				hits.Add(header.SessionId);
		}
		return new SessionFullScanResult(hits, toScan.Count, capped);
	}
}
