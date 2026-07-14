using System.Text.Json;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Models;

namespace PetBox.Log.Core.SelfLogging;

// Pure routing: which destination log a single self-log candidate belongs to, and splitting a
// flush batch into per-destination groups. SystemLogger (SystemLogger.cs:58,64) always stamps
// SourceContext + (when nonzero) EventId into the candidate's Properties bag before it reaches
// the flusher — both keys are fixed points of KqlPropertyKeys.Normalize (no hostile chars), so
// they round-trip through PropertiesJsonSerializer unchanged and are safe to read back here.
static class SelfLogRouter
{
	// Category match is a PREFIX (see SelfLogRoute doc comment), EventId match is exact. First
	// matching rule wins; no match falls back to the default self-log.
	public static string Resolve(LogEntryCandidate candidate, IReadOnlyList<SelfLogRoute> routes)
	{
		if (routes.Count == 0)
			return LogNames.SelfLog;

		using var doc = JsonDocument.Parse(candidate.Properties);
		var root = doc.RootElement;

		if (!root.TryGetProperty("EventId", out var eventIdEl) || eventIdEl.ValueKind != JsonValueKind.Number)
			return LogNames.SelfLog;
		if (!root.TryGetProperty("SourceContext", out var categoryEl) || categoryEl.ValueKind != JsonValueKind.String)
			return LogNames.SelfLog;

		var eventId = eventIdEl.GetInt32();
		var category = categoryEl.GetString() ?? "";

		foreach (var route in routes)
			if (route.EventId == eventId && category.StartsWith(route.Category, StringComparison.Ordinal))
				return route.Destination;

		return LogNames.SelfLog;
	}

	// Splits a flush batch by destination, preserving each destination group's relative order
	// (IEnumerable.GroupBy is stable). A mixed batch yields one group per distinct destination —
	// every candidate lands in exactly one group, so nothing is dropped by the split itself.
	public static IEnumerable<IGrouping<string, LogEntryCandidate>> Split(
		IReadOnlyList<LogEntryCandidate> batch, IReadOnlyList<SelfLogRoute> routes) =>
		batch.GroupBy(c => Resolve(c, routes));
}
