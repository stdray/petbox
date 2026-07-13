using PetBox.Core.Models;

namespace PetBox.Log.Core.Data;

// Picks which log a Logs/Traces page selects when the request carries no explicit
// ?log=/?logName= (work logs-traces-default-log). The bug this fixes: a plain page load with
// no log resolved rendered the SAME visible emptiness as a project that genuinely has no
// telemetry — that indistinguishability, not a crash, is what fooled the maintainer into
// thinking a live self-log ($system/petbox, full of spans) had no data. Reproduced on
// $system: three logs existed (cc-telemetry, petbox, prompt-rag-audit) and picking
// "alphabetically first" landed on cc-telemetry, a short-lived telemetry spike with events
// but ZERO spans, while petbox (the long-running self-log, created weeks earlier) held every
// trace. A newer log's name sorting earlier must never let it silently outrank the
// project's established one.
//
// Order: (1) an explicit `requested` name, if it names a log that exists — never overridden;
// (2) the sole log, if there is only one; (3) a log literally named "default"
// (LogNames.Default) — the ingestion convention for an unrouted write; (4) failing both, the
// OLDEST log by CreatedAt (ties broken by Name) — a project's first log is its established
// one; logs created later tend to be short-lived spikes/experiments layered on top of it.
public static class DefaultLogSelector
{
	public static string? Resolve(IReadOnlyList<LogMeta> logs, string? requested)
	{
		if (logs.Count == 0) return null;

		if (!string.IsNullOrWhiteSpace(requested))
		{
			var requestedMatch = logs.FirstOrDefault(l => string.Equals(l.Name, requested, StringComparison.Ordinal));
			if (requestedMatch is not null) return requestedMatch.Name;
		}

		if (logs.Count == 1) return logs[0].Name;

		var byConventionalName = logs.FirstOrDefault(l => string.Equals(l.Name, LogNames.Default, StringComparison.Ordinal));
		if (byConventionalName is not null) return byConventionalName.Name;

		return logs
			.OrderBy(l => l.CreatedAt)
			.ThenBy(l => l.Name, StringComparer.Ordinal)
			.First()
			.Name;
	}
}
