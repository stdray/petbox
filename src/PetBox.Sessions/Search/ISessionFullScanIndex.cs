namespace PetBox.Sessions.Search;

// The full-scan escape hatch (spec: session-fullscan-optin): a raw, UNTOKENIZED substring
// scan over every session's content — catches an exact phrase or a substring straddling
// token boundaries (inside a single long identifier) that both the digest leg and the
// stemmed term-FTS leg (ISessionTermIndex, whole-word prefix matching) miss. Deliberately
// expensive: every scanned session is fully hydrated and string-searched, so it is capped
// (never silently — a truncated scan is reported, not hidden) and gated behind an explicit
// per-call opt-in AND a two-key permission setting (SessionFullScanSettings) — never on by
// default, never automatic.
public interface ISessionFullScanIndex
{
	Task<SessionFullScanResult> ScanAsync(string projectKey, string query, CancellationToken ct = default);
}

// SessionIds carrying an exact case-insensitive substring match, most-recently-updated
// first (the scan order, also this leg's fusion ranking). `SessionsScanned` is how many
// sessions were actually opened and searched this call; `Capped` is true when the project
// holds MORE sessions than the scan cap, so some were never looked at — surfaced so a
// caller (and the log) can see the truncation, never silent.
public sealed record SessionFullScanResult(IReadOnlyList<string> SessionIds, int SessionsScanned, bool Capped);

// The scan cap: bound from config `Search:Sessions:FullScan:*`, conservative default.
public sealed record SessionFullScanOptions
{
	public int MaxSessions { get; init; } = 200;
}
